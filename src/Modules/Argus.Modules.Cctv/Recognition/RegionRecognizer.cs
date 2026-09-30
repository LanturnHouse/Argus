namespace Argus.Modules.Cctv;

/// <summary>비전 모델에 연결하지 못했거나 응답이 없다 (서버가 꺼졌거나 모델이 내려갔거나 시간 초과). 이미지는 실패 처리하지 않고 나중에 다시 시도한다.</summary>
public sealed class VisionUnavailableException(string message) : Exception(message);

/// <summary>모델이 응답했지만 기대한 모양(표 행, 도킹 숫자)이 아니다. 같은 이미지를 몇 번 다시 시도해 보고 안 되면 실패로 기록한다.</summary>
public sealed class RecognitionFailedException(string message) : Exception(message);

/// <summary>
/// 이미지 한 장의 인식 영역 하나를 비전 모델로 읽어 관측(Observation)으로 만든다.
/// 영역을 잘라 원본 크롭을 저장하고(판정 근거 화면에서 보여 준다), 그 크롭을 모델에 보낸다.
/// 같은 눈깔·같은 영역의 직전 크롭과 픽셀이 완전히 같으면 모델을 부르지 않고 직전 결과를 그대로 쓴다 (화면이 안 변한 프레임이 많아 큰 절약).
/// </summary>
public sealed class RegionRecognizer(VisionClient vision, Func<VisionSettings> settings, string cropRoot)
{
    private readonly Dictionary<string, (ulong Hash, Observation Observation)> _last = [];

    /// <summary>영역 설정이 바뀌었거나 분석을 껐다 켰을 때 직전 결과 재사용을 비운다.</summary>
    public void ResetReuse() => _last.Clear();

    public int ModelCalls { get; private set; }
    public int ReusedCalls { get; private set; }

    /// <summary>퍼센트 영역을 이미지 픽셀 영역으로 (이미지 밖으로 나가면 잘라 맞춘다).</summary>
    public static SourceBox ClampRegion(double x, double y, double w, double h, int width, int height)
    {
        int left = Math.Max(0, Math.Min(width - 1, (int)Math.Floor(width * x / 100)));
        int top = Math.Max(0, Math.Min(height - 1, (int)Math.Floor(height * y / 100)));
        int cw = Math.Max(1, Math.Min(width - left, (int)Math.Ceiling(width * w / 100)));
        int ch = Math.Max(1, Math.Min(height - top, (int)Math.Ceiling(height * h / 100)));
        return new SourceBox { Left = left, Top = top, Width = cw, Height = ch };
    }

    public async Task<Observation> RecognizeAsync(ImageRow image, Bitmap32 full, WatcherRegion region, int regionIndex, CancellationToken ct = default)
    {
        var box = ClampRegion(region.X, region.Y, region.W, region.H, full.Width, full.Height);
        var dir = Path.Combine(cropRoot, image.Id.ToString());
        var sourcePath = Path.GetFullPath(Path.Combine(dir, $"{regionIndex}-{region.Kind.Db()}-source.png"));

        var crop = ImageOps.Crop(full, box.Left, box.Top, box.Width, box.Height);
        var png = ImageOps.EncodePng(crop);
        Directory.CreateDirectory(dir);
        await File.WriteAllBytesAsync(sourcePath, png, ct).ConfigureAwait(false);

        var key = $"{image.Character}|{region.WatcherId}|{regionIndex}|{region.Kind.Db()}";
        var hash = ImageOps.Hash(crop);
        if (_last.TryGetValue(key, out var previous) && previous.Hash == hash)
        {
            ReusedCalls++;
            var copy = CctvJson.Deserialize<RegionPayload>(CctvJson.Serialize(previous.Observation.Payload)) ?? new RegionPayload();
            copy.RegionIndex = regionIndex; copy.SourceBox = box; copy.SourceCropPath = sourcePath; copy.Method = "reused";
            return new Observation { WatcherId = region.WatcherId, Kind = region.Kind, Confidence = previous.Observation.Confidence, Payload = copy };
        }

        ModelCalls++;
        var answer = await vision.RecognizeAsync(region.Kind, png, settings(), ct).ConfigureAwait(false);
        if (answer == null) throw new VisionUnavailableException(vision.LastError ?? "비전 모델이 응답하지 않았습니다.");

        var blank = new RegionFields();
        var fields = TextRules.ShapeVisionFields(region.Kind, answer, blank, "");
        if (ReferenceEquals(fields, blank)) throw new RecognitionFailedException($"{region.Kind.Label()} 영역의 모델 응답이 기대한 모양이 아닙니다: {Short(answer.ToJsonString())}");

        var observation = new Observation
        {
            WatcherId = region.WatcherId, Kind = region.Kind, Confidence = 0.9,
            Payload = new RegionPayload { RawText = answer.ToJsonString(), Fields = fields, RegionIndex = regionIndex, SourceBox = box, SourceCropPath = sourcePath, Method = "vision" },
        };
        _last[key] = (hash, observation);
        return observation;
    }

    private static string Short(string s) => s.Length > 200 ? s[..200] + "…" : s;
}

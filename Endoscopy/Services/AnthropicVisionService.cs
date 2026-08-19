using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Endoscopy.Services;

public record VisionFinding(string FindingType, string Description, double Confidence);

/// <summary>
/// Yakalanan kareyi Claude'un görüntü (vision) yeteneğiyle analiz ettirir.
///
/// NOT: Şu an gerçek endoskopi kamerası yerine dizüstü webcam'i kullanıldığı
/// için prompt bilinçli olarak "genel sahne analizi" istiyor, tıbbi tanı
/// koydurmuyor. Gerçek endoskopi görüntülerine geçildiğinde tek değişmesi
/// gereken yer promptSystem sabiti ve tool şemasındaki FindingType listesi.
/// </summary>
public class AnthropicVisionService
{
    private const string PromptSystem =
        "Bu bir yazılım/entegrasyon testidir: girdi gerçek bir endoskopi görüntüsü değil, " +
        "bir dizüstü bilgisayar webcam'inden alınmış test karesidir. Tıbbi tanı koyma, " +
        "hasta/doku yorumu yapma. Sadece görüntüde genel olarak neyi gördüğünü kısaca " +
        "raporla; bu, gerçek endoskopi görüntülerine geçildiğinde aynı veri hattının " +
        "çalıştığını doğrulamak için kullanılıyor.";

    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AnthropicVisionService> _logger;

    public AnthropicVisionService(HttpClient httpClient, IConfiguration configuration, ILogger<AnthropicVisionService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;

        _httpClient.BaseAddress = new Uri("https://api.anthropic.com/");
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_configuration["Anthropic:ApiKey"]);

    public async Task<VisionFinding?> AnalyzeFrameAsync(byte[] jpegBytes, CancellationToken cancellationToken = default)
    {
        var apiKey = _configuration["Anthropic:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogWarning("Anthropic:ApiKey ayarlanmamış, analiz atlanıyor.");
            return null;
        }

        var model = _configuration["Anthropic:Model"] ?? "claude-sonnet-5";
        var base64Image = Convert.ToBase64String(jpegBytes);

        var toolSchema = new JsonObject
        {
            ["name"] = "report_frame_analysis",
            ["description"] = "Analiz edilen karenin sonucunu yapılandırılmış olarak bildirir.",
            ["input_schema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["findingType"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Kısa sınıflandırma, örn: SUBJECT_DETECTED, NO_CLEAR_SUBJECT, LOW_QUALITY_IMAGE, OTHER"
                    },
                    ["description"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Görüntüde gözlemlenenin 1-2 cümlelik kısa, tarafsız açıklaması (Türkçe)."
                    },
                    ["confidence"] = new JsonObject
                    {
                        ["type"] = "number",
                        ["description"] = "0 ile 1 arasında güven skoru."
                    }
                },
                ["required"] = new JsonArray("findingType", "description", "confidence")
            }
        };

        var requestBody = new JsonObject
        {
            ["model"] = model,
            ["max_tokens"] = 512,
            ["system"] = PromptSystem,
            ["tools"] = new JsonArray(toolSchema),
            ["tool_choice"] = new JsonObject { ["type"] = "tool", ["name"] = "report_frame_analysis" },
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "image",
                            ["source"] = new JsonObject
                            {
                                ["type"] = "base64",
                                ["media_type"] = "image/jpeg",
                                ["data"] = base64Image
                            }
                        },
                        new JsonObject
                        {
                            ["type"] = "text",
                            ["text"] = "Bu kareyi analiz et ve report_frame_analysis aracını kullanarak sonucu bildir."
                        }
                    }
                }
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/messages");
        request.Headers.Add("x-api-key", apiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");
        request.Content = JsonContent.Create(requestBody);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Anthropic API hata döndü ({Status}): {Body}", response.StatusCode, responseBody);
            return null;
        }

        return ParseToolUseResult(responseBody);
    }

    private VisionFinding? ParseToolUseResult(string responseJson)
    {
        using var doc = JsonDocument.Parse(responseJson);
        if (!doc.RootElement.TryGetProperty("content", out var contentArray))
        {
            return null;
        }

        foreach (var block in contentArray.EnumerateArray())
        {
            if (block.TryGetProperty("type", out var typeEl) && typeEl.GetString() == "tool_use" &&
                block.TryGetProperty("input", out var input))
            {
                var findingType = input.TryGetProperty("findingType", out var ft) ? ft.GetString() ?? "OTHER" : "OTHER";
                var description = input.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
                var confidence = input.TryGetProperty("confidence", out var c) && c.TryGetDouble(out var cv) ? cv : 0.0;

                return new VisionFinding(findingType, description, confidence);
            }
        }

        _logger.LogWarning("Anthropic yanıtında tool_use bloğu bulunamadı: {Body}", responseJson);
        return null;
    }
}

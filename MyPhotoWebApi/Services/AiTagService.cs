using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using MyPhotoWebApi.Helpers;
using MyPhotoWebApi.Models;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MyPhotoWebApi.Services
{
    /// <summary>
    /// Suggests tags for photos with a vision model and stores them after the folder-name tags in the photo's tags.
    /// The model sees the thumbnail from the database, and calls are paced to the configured rate.
    /// </summary>
    public class AiTagService
    {
        public const int MaxIdsPerRequest = 50;
        public const int MaxAiTags = 5;
        private const int MaxTagLength = 20;

        // Answers that say nothing about the photo.
        private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "照片", "图片", "图像", "相片", "画面", "场景", "背景", "前景", "特写", "其他", "其它", "未知", "无", "无法识别", "不确定",
            "东西", "物体", "标签", "高清", "模糊", "photo", "image", "picture", "pic", "photograph", "unknown", "none", "other",
            "background", "scene", "tag",
        };

        private static readonly string Prompt =
            $"你是相册打标签助手。看图后给出最多{MaxAiTags}个简体中文标签，描述照片里最主要的场景、物体、活动或地点。" +
            "每个标签是2到6个字的词语，不要句子，不要描述画质、颜色或拍摄手法，不要“照片”“图片”这类泛词。" +
            "能确定是著名地标或城市时直接写名称。只输出JSON，例如 {\"tags\":[\"海滩\",\"日落\"]}。";

        // Waiting longer than this for the rate limit fails the photo instead of holding the request open.
        private static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(60);

        private readonly ILogger<AiTagService> _logger;
        private readonly VlmSettings _settings;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IMongoCollection<Photo> _photosCollection;
        private readonly string? _apiKey;

        private readonly SemaphoreSlim _pace = new(1, 1);
        private DateTime _nextCallAt = DateTime.MinValue;

        public AiTagService(ILogger<AiTagService> logger, IMongoDatabase mongoDatabase, MyPhotoSettings myPhotoSettings, IHttpClientFactory httpClientFactory)
        {
            _logger = logger;
            _settings = myPhotoSettings.Vlm;
            _httpClientFactory = httpClientFactory;
            _photosCollection = mongoDatabase.GetCollection<Photo>("photos");
            var keyFile = _settings.ApiKeyLocation;
            if (!string.IsNullOrWhiteSpace(keyFile) && File.Exists(keyFile))
            {
                _apiKey = File.ReadAllText(keyFile).Trim();
            }
            if (string.IsNullOrEmpty(_apiKey))
            {
                _apiKey = null;
                _logger.LogWarning($"AI tagging is off: no API key at MyPhotoSettings.Vlm.ApiKeyLocation '{keyFile}'.");
            }
        }

        public bool IsConfigured => _apiKey != null;

        /// <summary>Tags each photo in turn; every id gets its own result, and a photo that fails keeps its tags.</summary>
        public async Task<AiTagResult> TagPhotos(IEnumerable<string> ids, bool regenerate, CancellationToken ct)
        {
            var result = new AiTagResult();
            foreach (var id in ids.Distinct())
            {
                var item = new AiTagPhotoResult { Id = id };
                result.Results.Add(item);

                var photo = ObjectId.TryParse(id, out _) ? await _photosCollection.Find(p => p.Id == id).FirstOrDefaultAsync(ct) : null;
                if (photo == null)
                {
                    item.Status = AiTagPhotoResult.NotFound;
                    continue;
                }
                if (!regenerate && Util.ExtraTags(photo.Tags, photo.Path).Length > 0)
                {
                    item.Status = AiTagPhotoResult.Skipped;
                    item.Reason = AiTagPhotoResult.AlreadyTagged;
                    item.Tags = photo.Tags;
                    continue;
                }
                if (photo.Thumbnail == null || photo.Thumbnail.Length == 0)
                {
                    item.Status = AiTagPhotoResult.Skipped;
                    item.Reason = AiTagPhotoResult.NoThumbnail;
                    item.Tags = photo.Tags;
                    continue;
                }

                try
                {
                    var added = CleanTags(await AskModel(photo.Thumbnail, ct), photo.Path);
                    if (added.Length == 0) throw new InvalidOperationException("The model gave no usable tags.");
                    var tags = Util.GenerateTags(photo.Path ?? "").Concat(added).ToArray();
                    // Matches nothing if the photo moved meanwhile, so the move's tags are not overwritten.
                    var update = await _photosCollection.UpdateOneAsync(p => p.Id == id && p.Path == photo.Path,
                        Builders<Photo>.Update.Set(p => p.Tags, tags), cancellationToken: ct);
                    if (update.MatchedCount == 0) throw new InvalidOperationException("The photo moved while it was being tagged.");
                    item.Status = AiTagPhotoResult.Tagged;
                    item.Tags = tags;
                    item.Added = added;
                    _logger.LogInformation($"AI tagged photo {id}: {string.Join(", ", added)}");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception e)
                {
                    _logger.LogError(e, $"Failed to AI tag photo {id}");
                    item.Status = AiTagPhotoResult.Failed;
                    item.Error = e.Message;
                }
            }
            return result;
        }

        /// <summary>
        /// The model's suggestions, trimmed of spaces, "#" and punctuation, lower-cased, without stop words, the folder
        /// names of <paramref name="path"/> and repeats, and at most <see cref="MaxAiTags"/> of them.
        /// </summary>
        internal static string[] CleanTags(IEnumerable<string?> suggestions, string? path)
        {
            var pathTags = Util.GenerateTags(path ?? "");
            var tags = new List<string>();
            foreach (var suggestion in suggestions)
            {
                var tag = Regex.Replace(suggestion ?? "", @"\s+", " ");
                tag = new string(tag.SkipWhile(IsTrimmed).Reverse().SkipWhile(IsTrimmed).Reverse().ToArray()).ToLowerInvariant();
                if (tag.Length == 0 || tag.Length > MaxTagLength || StopWords.Contains(tag)) continue;
                if (pathTags.Contains(tag, StringComparer.OrdinalIgnoreCase) || tags.Contains(tag)) continue;
                tags.Add(tag);
                if (tags.Count == MaxAiTags) break;
            }
            return tags.ToArray();

            static bool IsTrimmed(char c) => char.IsWhiteSpace(c) || char.IsPunctuation(c) || char.IsSymbol(c);
        }

        /// <summary>Sends the image to the model and returns its raw suggestions; retries once after a rate limit or server error.</summary>
        private async Task<string[]> AskModel(byte[] image, CancellationToken ct)
        {
            var mime = image.Length > 4 && image[0] == 0x89 && image[1] == 0x50 ? "image/png" : "image/jpeg";
            var body = new JObject
            {
                ["model"] = _settings.Model,
                ["messages"] = new JArray
                {
                    new JObject { ["role"] = "system", ["content"] = Prompt },
                    new JObject
                    {
                        ["role"] = "user",
                        ["content"] = new JArray
                        {
                            new JObject
                            {
                                ["type"] = "image_url",
                                ["image_url"] = new JObject { ["url"] = $"data:{mime};base64,{Convert.ToBase64String(image)}" },
                            },
                        },
                    },
                },
                ["response_format"] = JObject.Parse(
                    "{\"type\":\"json_schema\",\"json_schema\":{\"name\":\"photo_tags\",\"schema\":{\"type\":\"object\"," +
                    "\"properties\":{\"tags\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}}},\"required\":[\"tags\"]}}}"),
            };
            if (!string.IsNullOrWhiteSpace(_settings.ReasoningEffort)) body["reasoning_effort"] = _settings.ReasoningEffort;

            var client = _httpClientFactory.CreateClient(nameof(AiTagService));
            for (var attempt = 0; ; attempt++)
            {
                await WaitTurn(ct);
                using var request = new HttpRequestMessage(HttpMethod.Post, _settings.BaseUrl.TrimEnd('/') + "/chat/completions")
                {
                    Content = new StringContent(body.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8, "application/json"),
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
                using var response = await client.SendAsync(request, ct);
                var text = await response.Content.ReadAsStringAsync(ct);
                if (response.IsSuccessStatusCode)
                {
                    var content = (string?)JObject.Parse(text).SelectToken("choices[0].message.content") ?? "";
                    return ParseSuggestions(content);
                }

                var status = (int)response.StatusCode;
                var retryable = response.StatusCode == HttpStatusCode.TooManyRequests || status >= 500;
                if (retryable)
                {
                    // Every later call waits too, so a rate limit holds back all requests, not only this one.
                    var delay = RetryDelay(response, text) ?? TimeSpan.FromSeconds(10);
                    await PushBack(delay);
                }
                if (!retryable || attempt > 0)
                {
                    throw new InvalidOperationException($"The model returned {status}: {(text.Length > 300 ? text[..300] : text)}");
                }
                _logger.LogWarning($"The model returned {status}; retrying once.");
            }
        }

        /// <summary>The "tags" array of the model's JSON answer, which may be wrapped in a ```json fence.</summary>
        internal static string[] ParseSuggestions(string content)
        {
            var start = content.IndexOf('{');
            var end = content.LastIndexOf('}');
            if (start < 0 || end < start) throw new InvalidOperationException($"The model did not answer in JSON: {content}");
            var tags = JObject.Parse(content[start..(end + 1)])["tags"] as JArray
                ?? throw new InvalidOperationException($"The model's answer has no tags: {content}");
            return tags.Select(t => t.Type == JTokenType.String ? (string?)t : null).Where(t => t != null).ToArray()!;
        }

        /// <summary>Waits until the next call may start, or fails at once when that is too far off.</summary>
        private async Task WaitTurn(CancellationToken ct)
        {
            await _pace.WaitAsync(ct);
            try
            {
                var wait = _nextCallAt - DateTime.UtcNow;
                if (wait > MaxWait)
                {
                    throw new InvalidOperationException($"The model's rate limit was reached; try again after {_nextCallAt.ToLocalTime():HH:mm:ss}.");
                }
                if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
                _nextCallAt = DateTime.UtcNow + TimeSpan.FromMinutes(1.0 / Math.Max(1, _settings.MaxRequestsPerMinute));
            }
            finally
            {
                _pace.Release();
            }
        }

        private async Task PushBack(TimeSpan delay)
        {
            await _pace.WaitAsync();
            try
            {
                var until = DateTime.UtcNow + delay;
                if (until > _nextCallAt) _nextCallAt = until;
            }
            finally
            {
                _pace.Release();
            }
        }

        /// <summary>How long the API asks us to wait: the Retry-After header or Google's RetryInfo "retryDelay".</summary>
        private static TimeSpan? RetryDelay(HttpResponseMessage response, string body)
        {
            if (response.Headers.RetryAfter?.Delta is TimeSpan delta) return delta;
            var match = Regex.Match(body, "\"retryDelay\"\\s*:\\s*\"(\\d+(?:\\.\\d+)?)s\"");
            return match.Success ? TimeSpan.FromSeconds(double.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)) : null;
        }
    }
}

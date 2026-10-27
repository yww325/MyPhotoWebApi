namespace MyPhotoWebApi.Models
{
    public class MyPhotoSettings
    {
        public string RootFolder { get; set; }
        public string ConnectionString { get; set; }
        public string DatabaseName { get; set; }
        public string FileUrl { get; set; }
        public string UserPassLocation { get; set; }
        public VlmSettings Vlm { get; set; } = new();
    }

    /// <summary>The vision model that suggests AI tags, called through an OpenAI-compatible chat completions API.</summary>
    public class VlmSettings
    {
        /// <summary>Base URL of the OpenAI-compatible API; "chat/completions" is appended.</summary>
        public string BaseUrl { get; set; } = "https://generativelanguage.googleapis.com/v1beta/openai/";
        public string Model { get; set; } = "gemini-3.1-flash-lite";
        /// <summary>Absolute path of a file holding the API key, read at startup; AI tagging is off without it.</summary>
        public string? ApiKeyLocation { get; set; }
        /// <summary>Calls to the model start at most this often; Google's free tier allows only a few per minute.</summary>
        public int MaxRequestsPerMinute { get; set; } = 10;
        /// <summary>Sent as reasoning_effort; "minimal" keeps thinking tokens, which are billed as output, near zero.</summary>
        public string? ReasoningEffort { get; set; } = "minimal";
        /// <summary>Sent as thinking.type; "disabled" skips thinking, which tagging does not need. Left out when blank.</summary>
        public string? Thinking { get; set; }
        /// <summary>Sent as image_url.detail; "low" bills a thumbnail as a few hundred tokens instead of a few thousand. Left out when blank.</summary>
        public string? Detail { get; set; }
    }
}

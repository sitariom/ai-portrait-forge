using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using Verse;

namespace Avatar
{
    /// <summary>
    /// External API image-to-image portrait generation.
    /// All HTTP calls run on background threads; callbacks fire on the main thread
    /// via LongEventHandler.ExecuteWhenFinished.
    /// </summary>
    public static class ApiClient
    {
        // =========================================================================
        // =========================================================================
        // Public entry point. Called from GeneratePortraitImmediate / ProcessPawn.
        //
        // imagePath   – local PNG file (480×576 pixel-art render from SaveToStaticPortrait)
        // prompts     – the combined prompt string from GetPrompts()
        // outputPath  – where to save the result PNG (same path as imagePath)
        // onComplete  – called on MAIN thread with (success, errorMessage)
        // startedUtc  – timestamp captured before the call, for elapsed-time telemetry
        // negativePrompt – if provided, used directly; otherwise falls back to human negative
        // =========================================================================
        public static void GeneratePortraitAsync(
            string imagePath, string prompts, string outputPath,
            Action<bool, string> onComplete, DateTime startedUtc,
            string negativePrompt = null)
        {
            // Snapshot everything we need on the calling thread.
            // Do NOT touch ModSettings or Unity APIs from the background thread.
            string apiKey = null;
            string endpoint = null;
            string positiveStylePrefix = null;
            string positiveStyleSuffix = null;
            // negativePrompt is the method parameter — used directly
            float cfgScale = 7f;
            int steps = 30;
            string sampler = "";
            string scheduler = "";
            string stylePreset = "";
            string model = "";
            ApiProvider provider = ApiProvider.StabilityAI;
            bool prependPositive = false;
            string requestTemplate = "";
            string responseImagePath = "output";

            try
            {
                AvatarMod m = LoadedModManager.GetMod<AvatarMod>();
                if (m != null)
                {
                    AvatarSettings s = m.settings;
                    provider = s.apiProvider;
                    cfgScale = s.apiCfgScale;
                    steps = s.apiSteps;
                    sampler = s.apiSampler;
                    scheduler = s.apiScheduler;
                    stylePreset = s.apiStylePreset;
                    prependPositive = s.apiPrependPositive;

                    // Read provider-specific fields
                    switch (provider)
                    {
                        case ApiProvider.GoogleGemini:
                            apiKey = s.geminiApiKey?.Trim();
                            model = s.geminiModel?.Trim();
                            break;
                        case ApiProvider.OpenRouter:
                            apiKey = s.openRouterApiKey?.Trim();
                            model = s.openRouterModel?.Trim();
                            break;
                        case ApiProvider.Pixazo:
                            apiKey = s.pixazoApiKey?.Trim();
                            model = s.pixazoModel?.Trim();
                            break;
                        case ApiProvider.NagaAc:
                            apiKey = s.nagaAcApiKey?.Trim();
                            model = s.nagaAcModel?.Trim();
                            break;
                        case ApiProvider.StabilityAI:
                            apiKey = s.stabilityApiKey?.Trim();
                            endpoint = s.stabilityEndpoint?.Trim();
                            break;
                        case ApiProvider.Pollinations:
                            apiKey = s.pollinationsApiKey?.Trim();
                            model = s.pollinationsModel?.Trim();
                            break;
                        case ApiProvider.Generic:
                            apiKey = s.genericApiKey?.Trim();
                            endpoint = s.genericEndpoint?.Trim();
                            model = s.genericModel?.Trim();
                            requestTemplate = s.genericRequestTemplate;
                            responseImagePath = s.genericResponseImagePath;
                            break;
                    }

                    // Resolve art style prompts
                    string artPrompt = AvatarMod.GetArtStylePrompt(s.artStyle, s.customStylePrompt);
                    positiveStyleSuffix = artPrompt;
                    // Use caller-supplied negativePrompt, or fall back to human default
                    if (negativePrompt == null)
                        negativePrompt = AvatarMod.GetFullNegativePrompt(s);
                    if (!string.IsNullOrEmpty(s.apiPositiveStylePrompt))
                    {
                        if (prependPositive)
                            positiveStylePrefix = s.apiPositiveStylePrompt;
                        else
                            positiveStyleSuffix = s.apiPositiveStylePrompt;
                    }
                }
            }
            catch (Exception e)
            {
                LongEventHandler.ExecuteWhenFinished(() => onComplete(false, "Failed to read settings: " + e.Message));
                return;
            }

            // Pollinations allows empty API key (free tier works without auth)
            bool apiKeyRequired = provider != ApiProvider.Pollinations;
            if (apiKeyRequired && string.IsNullOrEmpty(apiKey))
            {
                string providerName = provider switch
                {
                    ApiProvider.GoogleGemini => "Google Gemini",
                    ApiProvider.OpenRouter => "OpenRouter",
                    ApiProvider.Pixazo => "Pixazo",
                    ApiProvider.NagaAc => "Naga.ac",
                    ApiProvider.StabilityAI => "StabilityAI",
                    ApiProvider.Pollinations => "Pollinations.ai",
                    ApiProvider.Generic => "Generic API",
                    _ => "unknown"
                };
                LongEventHandler.ExecuteWhenFinished(() =>
                    onComplete(false, provider == ApiProvider.GoogleGemini
                        ? "API key not configured for " + providerName + ". Get one at https://aistudio.google.com/apikey"
                        : "API key not configured for " + providerName + ". Open Mod Options → AI Portrait Forge to set it."));
                return;
            }

            // Capture everything for the background thread
            string capturedEndpoint = endpoint;
            string capturedApiKey = apiKey;
            string capturedPrompts = prompts;
            string capturedPrefix = positiveStylePrefix;
            string capturedSuffix = positiveStyleSuffix;
            string capturedNegative = negativePrompt;
            float capturedCfg = cfgScale;
            int capturedSteps = steps;
            string capturedSampler = sampler;
            string capturedScheduler = scheduler;
            string capturedStylePreset = stylePreset;
            string capturedModel = model;
            string capturedRequestTemplate = requestTemplate;
            string capturedResponseImagePath = responseImagePath;
            ApiProvider capturedProvider = provider;
            bool capturedPrepend = prependPositive;
            string capturedImagePath = imagePath;
            string capturedOutputPath = outputPath;

            ThreadPool.QueueUserWorkItem(_ =>
            {
                bool success = false;
                string error = null;
                try
                {
                    // Read the pixel-art PNG into memory (skip if file doesn't exist or provider is text-only)
                    byte[] imageBytes = null;
                    bool hasImageFile = File.Exists(capturedImagePath);
                    if (hasImageFile && capturedProvider != ApiProvider.Pixazo && capturedProvider != ApiProvider.Pollinations)
                    {
                        imageBytes = File.ReadAllBytes(capturedImagePath);
                    }

                    if (capturedProvider == ApiProvider.StabilityAI)
                    {
                        if (imageBytes == null)
                        {
                            error = "Stability AI requires an input image. Portrait generation failed.";
                            goto done;
                        }
                        success = CallStabilityAI(
                            capturedEndpoint, capturedApiKey, imageBytes,
                            capturedPrompts, capturedPrefix, capturedSuffix,
                            capturedNegative, capturedPrepend,
                            capturedCfg, capturedSteps, capturedSampler,
                            capturedStylePreset, capturedOutputPath,
                            out error);
                    }
                    else if (capturedProvider == ApiProvider.GoogleGemini)
                    {
                        success = CallGoogleGemini(
                            capturedApiKey, imageBytes,
                            capturedPrompts, capturedPrefix, capturedSuffix,
                            capturedNegative,
                            capturedModel, capturedOutputPath,
                            out error);
                    }
                    else if (capturedProvider == ApiProvider.OpenRouter)
                    {
                        success = CallOpenRouter(
                            capturedApiKey, imageBytes,
                            capturedPrompts, capturedPrefix, capturedSuffix,
                            capturedNegative,
                            capturedModel, capturedOutputPath,
                            out error);
                    }
                    else if (capturedProvider == ApiProvider.Pixazo)
                    {
                        success = CallPixazo(
                            capturedApiKey,
                            capturedPrompts, capturedPrefix, capturedSuffix,
                            capturedNegative,
                            capturedModel, capturedOutputPath,
                            out error);
                    }
                    else if (capturedProvider == ApiProvider.NagaAc)
                    {
                        success = CallNagaAc(
                            capturedApiKey, capturedModel,
                            capturedPrompts, capturedPrefix, capturedSuffix,
                            capturedNegative,
                            capturedOutputPath, out error);
                    }
                    else if (capturedProvider == ApiProvider.Pollinations)
                    {
                        success = CallPollinations(
                            capturedApiKey,
                            capturedPrompts, capturedPrefix, capturedSuffix,
                            capturedNegative, capturedPrepend,
                            capturedModel,
                            capturedOutputPath, out error);
                    }
                    else
                    {
                        success = CallGenericApi(
                            capturedEndpoint, capturedApiKey, imageBytes,
                            capturedPrompts, capturedPrefix, capturedSuffix,
                            capturedNegative, capturedPrepend,
                            capturedCfg, capturedSteps, capturedSampler,
                            capturedScheduler, capturedModel,
                            capturedRequestTemplate, capturedResponseImagePath,
                            capturedOutputPath, out error);
                    }
                    done:;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    Log.Warning("Avatar API: generation threw: " + ex);
                }

                bool capturedSuccess = success;
                string capturedError = error;
                LongEventHandler.ExecuteWhenFinished(() =>
                    onComplete(capturedSuccess, capturedError));
            });
        }

        // =========================================================================
        // Stability AI REST API (img2img)
        // Docs: https://platform.stability.ai/docs/api-reference#tag/Edit/paths/~1v1~1generation~1{engine_id}~1image-to-image/post
        //
        // Uses multipart/form-data as required by the API.
        // The prompt is built as: [prefix] + prompts + [suffix], sent as
        // a positive text_prompt. Negative prompt + cfg/steps/style_preset
        // are also forwarded.
        // =========================================================================
        private static bool CallStabilityAI(
            string endpoint, string apiKey, byte[] imageBytes,
            string prompts, string prefix, string suffix,
            string negativePrompt, bool prependPositive,
            float cfgScale, int steps, string sampler,
            string stylePreset, string outputPath,
            out string error)
        {
            error = null;

            // Build the positive prompt
            string positive;
            if (prependPositive)
                positive = (prefix + " " + prompts + " " + suffix).Trim();
            else
                positive = (prompts + " " + suffix).Trim();
            // Collapse multiple spaces
            while (positive.Contains("  ")) positive = positive.Replace("  ", " ");

            string boundary = "----AvatarModBoundary" + Guid.NewGuid().ToString("N");

            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(endpoint);
            req.Method = "POST";
            req.ContentType = "multipart/form-data; boundary=" + boundary;
            req.Headers["Authorization"] = "Bearer " + apiKey;
            req.Accept = "application/json";
            req.Timeout = 180000; // 3 minutes — SDXL can be slow on shared GPUs

            // Build multipart body
            using (MemoryStream bodyStream = new MemoryStream())
            {
                byte[] boundaryBytes = Encoding.UTF8.GetBytes("--" + boundary + "\r\n");
                byte[] crlf = Encoding.UTF8.GetBytes("\r\n");

                // text_prompts[0] — positive
                WriteMultipartField(bodyStream, boundaryBytes, crlf,
                    "text_prompts[0][text]", positive);
                WriteMultipartField(bodyStream, boundaryBytes, crlf,
                    "text_prompts[0][weight]", "1.0");

                // text_prompts[1] — negative
                if (!string.IsNullOrEmpty(negativePrompt))
                {
                    WriteMultipartField(bodyStream, boundaryBytes, crlf,
                        "text_prompts[1][text]", negativePrompt);
                    WriteMultipartField(bodyStream, boundaryBytes, crlf,
                        "text_prompts[1][weight]", "-1.0");
                }

                // cfg_scale
                WriteMultipartField(bodyStream, boundaryBytes, crlf,
                    "cfg_scale", cfgScale.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));

                // steps
                WriteMultipartField(bodyStream, boundaryBytes, crlf,
                    "steps", steps.ToString());

                // clip_guidance_preset
                WriteMultipartField(bodyStream, boundaryBytes, crlf,
                    "clip_guidance_preset", "NONE");

                // samples
                WriteMultipartField(bodyStream, boundaryBytes, crlf,
                    "samples", "1");

                // sampler
                if (!string.IsNullOrEmpty(sampler))
                {
                    WriteMultipartField(bodyStream, boundaryBytes, crlf,
                        "sampler", sampler);
                }

                // style_preset
                if (!string.IsNullOrEmpty(stylePreset))
                {
                    WriteMultipartField(bodyStream, boundaryBytes, crlf,
                        "style_preset", stylePreset);
                }

                // init_image — the PNG file
                bodyStream.Write(boundaryBytes, 0, boundaryBytes.Length);
                string imageHeader = "Content-Disposition: form-data; name=\"init_image\"; filename=\"pawn.png\"\r\n" +
                                     "Content-Type: image/png\r\n\r\n";
                byte[] imageHeaderBytes = Encoding.UTF8.GetBytes(imageHeader);
                bodyStream.Write(imageHeaderBytes, 0, imageHeaderBytes.Length);
                bodyStream.Write(imageBytes, 0, imageBytes.Length);
                bodyStream.Write(crlf, 0, crlf.Length);

                // Closing boundary
                byte[] closingBoundary = Encoding.UTF8.GetBytes("--" + boundary + "--\r\n");
                bodyStream.Write(closingBoundary, 0, closingBoundary.Length);

                bodyStream.Flush();
                req.ContentLength = bodyStream.Length;

                using (Stream reqStream = req.GetRequestStream())
                {
                    bodyStream.Position = 0;
                    bodyStream.CopyTo(reqStream);
                }
            }

            // Send and parse response
            try
            {
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    if (resp.StatusCode != HttpStatusCode.OK)
                    {
                        error = "Stability AI returned HTTP " + (int)resp.StatusCode;
                        Log.Warning("Avatar API: " + error);
                        return false;
                    }

                    string responseJson;
                    using (StreamReader reader = new StreamReader(resp.GetResponseStream()))
                    {
                        responseJson = reader.ReadToEnd();
                    }

                    // Extract base64 image from artifacts[0].base64
                    string base64Image = ExtractJsonField(responseJson, "base64");
                    if (string.IsNullOrEmpty(base64Image))
                    {
                        error = "Stability AI response contained no base64 image data. Response: " +
                            (responseJson.Length > 300 ? responseJson.Substring(0, 300) + "..." : responseJson);
                        Log.Warning("Avatar API: " + error);
                        return false;
                    }

                    byte[] resultBytes = Convert.FromBase64String(base64Image);
                    File.WriteAllBytes(outputPath, resultBytes);
                    return true;
                }
            }
            catch (WebException we)
            {
                error = "Stability AI request failed: ";
                try
                {
                    if (we.Response != null)
                    {
                        using (StreamReader sr = new StreamReader(we.Response.GetResponseStream()))
                        {
                            string body = sr.ReadToEnd();
                            error += body;
                            if (error.Length > 500) error = error.Substring(0, 500) + "...";
                        }
                    }
                    else
                    {
                        error += we.Message;
                    }
                }
                catch { error += we.Message; }
                Log.Warning("Avatar API: " + error);
                return false;
            }
        }

        // =========================================================================
        // Generic JSON API (configurable via settings).
        //
        // Sends a JSON POST with placeholders replaced:
        //   {prompt}          — the full combined positive prompt
        //   {negative_prompt} — the negative prompt
        //   {image_base64}    — the pixel-art PNG encoded as base64
        //   {cfg_scale}       — CFG scale float
        //   {steps}           — step count int
        //   {width}           — 480
        //   {height}          — 576
        //
        // The response image field path is passed via the responseImagePath parameter.
        // (e.g., "artifacts.0.base64", "output", "data.image").
        // If the path value is a URL (starts with http), downloads it.
        // Otherwise treats it as base64.
        // =========================================================================
        private static bool CallGenericApi(
            string endpoint, string apiKey, byte[] imageBytes,
            string prompts, string prefix, string suffix,
            string negativePrompt, bool prependPositive,
            float cfgScale, int steps, string sampler,
            string scheduler, string model,
            string requestTemplate, string responseImagePath,
            string outputPath, out string error)
        {
            error = null;

            // Build the positive prompt
            string positive;
            if (prependPositive)
                positive = (prefix + " " + prompts + " " + suffix).Trim();
            else
                positive = (prompts + " " + suffix).Trim();
            while (positive.Contains("  ")) positive = positive.Replace("  ", " ");

            string imageBase64 = Convert.ToBase64String(imageBytes);

            if (string.IsNullOrEmpty(requestTemplate))
            {
                // Sensible default template for common APIs (Replicate-style)
                requestTemplate = "{\"version\":\"" + (model ?? "") + "\",\"input\":{\"image\":\"data:image/png;base64,{image_base64}\",\"prompt\":\"{prompt}\",\"negative_prompt\":\"{negative_prompt}\",\"cfg_scale\":{cfg_scale},\"steps\":{steps},\"width\":{width},\"height\":{height}}}";
            }
            if (string.IsNullOrEmpty(responseImagePath))
                responseImagePath = "output";

            string requestBody = requestTemplate
                .Replace("{prompt}", JsonEscapeValue(positive))
                .Replace("{negative_prompt}", JsonEscapeValue(negativePrompt ?? ""))
                .Replace("{image_base64}", imageBase64)
                .Replace("{cfg_scale}", cfgScale.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture))
                .Replace("{steps}", steps.ToString())
                .Replace("{width}", "480")
                .Replace("{height}", "576")
                .Replace("{sampler}", JsonEscapeValue(sampler ?? ""))
                .Replace("{scheduler}", JsonEscapeValue(scheduler ?? ""));

            byte[] bodyBytes = Encoding.UTF8.GetBytes(requestBody);

            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(endpoint);
            req.Method = "POST";
            req.ContentType = "application/json";
            req.Accept = "application/json";
            if (!string.IsNullOrEmpty(apiKey))
                req.Headers["Authorization"] = "Bearer " + apiKey;
            req.Timeout = 180000;
            req.ContentLength = bodyBytes.Length;

            using (Stream reqStream = req.GetRequestStream())
            {
                reqStream.Write(bodyBytes, 0, bodyBytes.Length);
            }

            try
            {
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    string responseJson;
                    using (StreamReader reader = new StreamReader(resp.GetResponseStream()))
                    {
                        responseJson = reader.ReadToEnd();
                    }

                    // Extract image from response
                    string imageValue = ExtractJsonField(responseJson, responseImagePath);
                    if (string.IsNullOrEmpty(imageValue))
                    {
                        error = "Could not find image at path '" + responseImagePath +
                            "' in API response. Response: " +
                            (responseJson.Length > 300 ? responseJson.Substring(0, 300) + "..." : responseJson);
                        Log.Warning("Avatar API: " + error);
                        return false;
                    }

                    if (imageValue.StartsWith("http://") || imageValue.StartsWith("https://"))
                    {
                        // Download from URL
                        using (WebClient wc = new WebClient())
                        {
                            wc.DownloadFile(imageValue, outputPath);
                        }
                    }
                    else
                    {
                        // Strip data URI prefix if present
                        string b64 = imageValue;
                        if (b64.StartsWith("data:image/"))
                        {
                            int commaIdx = b64.IndexOf(',');
                            if (commaIdx >= 0) b64 = b64.Substring(commaIdx + 1);
                        }
                        byte[] resultBytes = Convert.FromBase64String(b64);
                        File.WriteAllBytes(outputPath, resultBytes);
                    }
                    return true;
                }
            }
            catch (WebException we)
            {
                error = "API request failed: ";
                try
                {
                    if (we.Response != null)
                    {
                        using (StreamReader sr = new StreamReader(we.Response.GetResponseStream()))
                        {
                            string body = sr.ReadToEnd();
                            error += body;
                            if (error.Length > 500) error = error.Substring(0, 500) + "...";
                        }
                    }
                    else
                    {
                        error += we.Message;
                    }
                }
                catch { error += we.Message; }
                Log.Warning("Avatar API: " + error);
                return false;
            }
        }

        // =========================================================================
        // Google Gemini Image Generation API (Imagen / Nano Banana)
        //
        // Uses the generateContent REST endpoint to produce images.
        // Sends the pixel-art PNG as a reference image + text prompt.
        // Model: gemini-3.1-flash-image (Nano Banana) by default.
        // API docs: https://ai.google.dev/gemini-api/docs/image-generation
        // =========================================================================
        private static bool CallGoogleGemini(
            string apiKey, byte[] imageBytes,
            string prompts, string prefix, string suffix,
            string negativePrompt,
            string geminiModel, string outputPath,
            out string error)
        {
            error = null;

            // Force image-only output for Gemini
            string positive = ("Generate ONLY an image, no text: " + prompts + " " + suffix).Trim();
            if (!string.IsNullOrEmpty(prefix))
                positive = prefix + " " + positive;
            while (positive.Contains("  ")) positive = positive.Replace("  ", " ");

            // Add negative instructions if provided
            if (!string.IsNullOrEmpty(negativePrompt))
                positive += "\n\nIMPORTANT: " + negativePrompt;

            // Build the Gemini REST endpoint
            string endpoint = "https://generativelanguage.googleapis.com/v1beta/models/" +
                Uri.EscapeDataString(geminiModel) + ":generateContent?key=" +
                Uri.EscapeDataString(apiKey);

            // Build the JSON request body
            string imageBase64 = Convert.ToBase64String(imageBytes);
            string promptEscaped = JsonEscapeValue(positive);

            string requestJson = "{" +
                "\"contents\":[{\"parts\":[" +
                "{\"text\":\"" + promptEscaped + "\"}," +
                "{\"inlineData\":{\"mimeType\":\"image/png\",\"data\":\"" + imageBase64 + "\"}}" +
                "]}]," +
                "\"generationConfig\":{\"responseModalities\":[\"IMAGE\"]}" +
                "}";

            byte[] bodyBytes = Encoding.UTF8.GetBytes(requestJson);

            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(endpoint);
            req.Method = "POST";
            req.ContentType = "application/json";
            req.Timeout = 180000; // 3 minutes
            req.ContentLength = bodyBytes.Length;

            try
            {
                using (Stream reqStream = req.GetRequestStream())
                {
                    reqStream.Write(bodyBytes, 0, bodyBytes.Length);
                }

                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    string responseJson;
                    using (StreamReader reader = new StreamReader(resp.GetResponseStream()))
                    {
                        responseJson = reader.ReadToEnd();
                    }

                    // Extract image from response: candidates[0].content.parts[?].inlineData.data
                    // Find the part with inlineData (may not be the first part if text is also returned)
                    string base64Image = ExtractGeminiImage(responseJson);
                    if (string.IsNullOrEmpty(base64Image))
                    {
                        error = "Gemini response contained no image. The model may have returned text instead. Response: " +
                            (responseJson.Length > 400 ? responseJson.Substring(0, 400) + "..." : responseJson);
                        Log.Warning("Avatar API (Gemini): " + error);
                        return false;
                    }

                    byte[] resultBytes = Convert.FromBase64String(base64Image);
                    File.WriteAllBytes(outputPath, resultBytes);
                    return true;
                }
            }
            catch (WebException we)
            {
                error = "Gemini API request failed: ";
                try
                {
                    if (we.Response != null)
                    {
                        using (StreamReader sr = new StreamReader(we.Response.GetResponseStream()))
                        {
                            string body = sr.ReadToEnd();
                            error += body;
                            if (error.Length > 500) error = error.Substring(0, 500) + "...";
                        }
                    }
                    else
                    {
                        error += we.Message;
                    }
                }
                catch { error += we.Message; }
                Log.Warning("Avatar API (Gemini): " + error);
                return false;
            }
        }

        /// <summary>
        /// Extracts the first inlineData.data (base64 image) from a Gemini generateContent response.
        /// Walks candidates[0].content.parts[] looking for inlineData.
        /// </summary>
        private static string ExtractGeminiImage(string json)
        {
            // Simple approach: find "inlineData" and extract the "data" field after it.
            // More robust than the general JSON path extractor for this specific format.
            try
            {
                int inlineIdx = json.IndexOf("\"inlineData\"", StringComparison.Ordinal);
                if (inlineIdx < 0) return null;

                // Find "data" field within the inlineData object
                int dataIdx = json.IndexOf("\"data\"", inlineIdx, StringComparison.Ordinal);
                if (dataIdx < 0) return null;

                // Move past "data":"
                int colonIdx = json.IndexOf(':', dataIdx);
                if (colonIdx < 0) return null;
                int startQuote = json.IndexOf('"', colonIdx + 1);
                if (startQuote < 0) return null;

                // Find the closing quote (base64 has no quotes inside)
                int endQuote = json.IndexOf('"', startQuote + 1);
                if (endQuote < 0) return null;

                return json.Substring(startQuote + 1, endQuote - startQuote - 1);
            }
            catch { return null; }
        }

        // =========================================================================
        // OpenRouter API — OpenAI-compatible chat completions
        //
        // Endpoint: POST https://openrouter.ai/api/v1/chat/completions
        // Auth: Bearer token
        // Uses the model selected by the user in settings.
        // Image is sent as a data: URL in the message content.
        // =========================================================================
        private const string OpenRouterBaseUrl = "https://openrouter.ai/api/v1/chat/completions";

        private static bool CallOpenRouter(
            string apiKey, byte[] imageBytes,
            string prompts, string prefix, string suffix,
            string negativePrompt,
            string model, string outputPath, out string error)
        {
            error = null;

            string positive = (prompts + " " + suffix).Trim();
            if (!string.IsNullOrEmpty(prefix))
                positive = prefix + " " + positive;
            while (positive.Contains("  ")) positive = positive.Replace("  ", " ");

            if (!string.IsNullOrEmpty(negativePrompt))
                positive += "\n\nIMPORTANT: " + negativePrompt;

            string imageBase64 = Convert.ToBase64String(imageBytes);
            string imageUrl = "data:image/png;base64," + imageBase64;

            if (string.IsNullOrEmpty(model))
            {
                error = "No model selected for OpenRouter.";
                return false;
            }

            return TryOpenRouterModel(apiKey, model, positive, imageUrl, outputPath, out error);
        }

        private static bool TryOpenRouterModel(
            string apiKey, string model, string prompt, string imageUrl,
            string outputPath, out string error)
        {
            error = null;

            // Build OpenAI-compatible chat completion request
            string requestJson = "{" +
                "\"model\":\"" + JsonEscapeValue(model) + "\"," +
                "\"messages\":[{" +
                "\"role\":\"user\"," +
                "\"content\":[" +
                "{\"type\":\"text\",\"text\":\"" + JsonEscapeValue(prompt) + "\"}," +
                "{\"type\":\"image_url\",\"image_url\":{\"url\":\"" + JsonEscapeValue(imageUrl) + "\"}}" +
                "]" +
                "}]" +
                "}";

            byte[] bodyBytes = Encoding.UTF8.GetBytes(requestJson);

            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(OpenRouterBaseUrl);
            req.Method = "POST";
            req.ContentType = "application/json";
            req.Headers["Authorization"] = "Bearer " + apiKey;
            req.Headers["HTTP-Referer"] = "https://github.com/sitariom/ai-portrait-forge";
            req.Headers["X-Title"] = "AI Portrait Forge (RimWorld Mod)";
            req.Timeout = 180000;
            req.ContentLength = bodyBytes.Length;

            using (Stream reqStream = req.GetRequestStream())
            {
                reqStream.Write(bodyBytes, 0, bodyBytes.Length);
            }

            try
            {
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    string responseJson;
                    using (StreamReader reader = new StreamReader(resp.GetResponseStream()))
                    {
                        responseJson = reader.ReadToEnd();
                    }

                    // Extract image from OpenAI-compatible response
                    string base64Image = ExtractOpenAIImage(responseJson);
                    if (string.IsNullOrEmpty(base64Image))
                    {
                        error = "OpenRouter response contained no image. Response: " +
                            (responseJson.Length > 300 ? responseJson.Substring(0, 300) + "..." : responseJson);
                        Log.Warning("Avatar API (OpenRouter): " + error);
                        return false;
                    }

                    byte[] resultBytes = Convert.FromBase64String(base64Image);
                    File.WriteAllBytes(outputPath, resultBytes);
                    return true;
                }
            }
            catch (WebException we)
            {
                error = "OpenRouter request failed: ";
                try
                {
                    if (we.Response != null)
                    {
                        using (StreamReader sr = new StreamReader(we.Response.GetResponseStream()))
                        {
                            string body = sr.ReadToEnd();
                            error += body;
                            if (error.Length > 500) error = error.Substring(0, 500) + "...";
                        }
                    }
                    else
                    {
                        error += we.Message;
                    }
                }
                catch { error += we.Message; }
                Log.Warning("Avatar API (OpenRouter): " + error);
                return false;
            }
        }

        /// <summary>
        /// Extracts base64 image from an OpenAI-compatible chat completion response.
        /// Image appears in choices[0].message.content as a data: URL or in dedicated image field.
        /// </summary>
        private static string ExtractOpenAIImage(string json)
        {
            try
            {
                // Look for data:image pattern in the response (covers both content parts and dedicated image fields)
                int dataIdx = json.IndexOf("data:image/png;base64,", StringComparison.Ordinal);
                if (dataIdx < 0)
                    dataIdx = json.IndexOf("data:image/jpeg;base64,", StringComparison.Ordinal);
                if (dataIdx < 0)
                    dataIdx = json.IndexOf("data:image/webp;base64,", StringComparison.Ordinal);
                if (dataIdx < 0)
                {
                    // Try b64_json field (DALL-E style)
                    int b64Idx = json.IndexOf("\"b64_json\"", StringComparison.Ordinal);
                    if (b64Idx >= 0)
                    {
                        int colonIdx2 = json.IndexOf(':', b64Idx);
                        int startQ2 = json.IndexOf('"', colonIdx2 + 1);
                        int endQ2 = json.IndexOf('"', startQ2 + 1);
                        if (startQ2 >= 0 && endQ2 > startQ2)
                            return json.Substring(startQ2 + 1, endQ2 - startQ2 - 1);
                    }
                    return null;
                }

                // Find the comma after the mime type, then extract until closing quote
                int comma = json.IndexOf(',', dataIdx);
                if (comma < 0) return null;
                int start = comma + 1;
                int end = start;
                while (end < json.Length && json[end] != '"' && json[end] != '\n' && json[end] != '\r' && json[end] != ' ')
                    end++;
                return json.Substring(start, end - start).Trim();
            }
            catch { return null; }
        }

        // =========================================================================
        // Pixazo API — text-to-image with free models
        //
        // Models:
        //   sdxl-base       → SDXL (sync)
        //   sdxl-inpainting → Inpainting (sync, needs mask)
        // =========================================================================
        private static bool CallPixazo(
            string apiKey,
            string prompts, string prefix, string suffix,
            string negativePrompt,
            string pixModel, string outputPath,
            out string error)
        {
            error = null;

            string positive = (prompts + " " + suffix).Trim();
            if (!string.IsNullOrEmpty(prefix))
                positive = prefix + " " + positive;
            while (positive.Contains("  ")) positive = positive.Replace("  ", " ");

            string modelLower = (pixModel ?? "sdxl-base").ToLowerInvariant();

            string endpoint;
            string requestJson;

            if (modelLower.Contains("inpaint"))
            {
                endpoint = "https://gateway.pixazo.ai/inpainting/v1/getImage";
                requestJson = "{" +
                    "\"prompt\":\"" + JsonEscapeValue(positive) + "\"," +
                    "\"negativePrompt\":\"" + JsonEscapeValue(negativePrompt ?? "") + "\"," +
                    "\"height\":1024," +
                    "\"width\":1024," +
                    "\"num_steps\":20," +
                    "\"guidance\":5" +
                    "}";
            }
            else // sdxl-base (default)
            {
                endpoint = "https://gateway.pixazo.ai/getImage/v1/getSDXLImage";
                requestJson = "{" +
                    "\"prompt\":\"" + JsonEscapeValue(positive) + "\"," +
                    "\"negative_prompt\":\"" + JsonEscapeValue(negativePrompt ?? "") + "\"," +
                    "\"height\":1024," +
                    "\"width\":1024," +
                    "\"num_steps\":20," +
                    "\"guidance_scale\":5" +
                    "}";
            }

            byte[] bodyBytes = Encoding.UTF8.GetBytes(requestJson);

            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(endpoint);
            req.Method = "POST";
            req.ContentType = "application/json";
            req.Headers["Ocp-Apim-Subscription-Key"] = apiKey;
            req.Timeout = 180000;
            req.ContentLength = bodyBytes.Length;

            try
            {
                using (Stream reqStream = req.GetRequestStream())
                {
                    reqStream.Write(bodyBytes, 0, bodyBytes.Length);
                }

                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    string responseJson;
                    using (StreamReader reader = new StreamReader(resp.GetResponseStream()))
                    {
                        responseJson = reader.ReadToEnd();
                    }

                    string imageUrl = ExtractJsonField(responseJson, "imageUrl");
                    if (string.IsNullOrEmpty(imageUrl))
                        imageUrl = ExtractJsonField(responseJson, "output");
                    if (string.IsNullOrEmpty(imageUrl) || (!imageUrl.StartsWith("http://") && !imageUrl.StartsWith("https://")))
                    {
                        error = "Pixazo response contained no valid image URL. Response: " +
                            (responseJson.Length > 300 ? responseJson.Substring(0, 300) + "..." : responseJson);
                        Log.Warning("Avatar API (Pixazo): " + error);
                        return false;
                    }

                    using (WebClient wc = new WebClient())
                    {
                        wc.DownloadFile(imageUrl, outputPath);
                    }
                    return true;
                }
            }
            catch (WebException we)
            {
                error = "Pixazo request failed: ";
                try
                {
                    if (we.Response != null)
                    {
                        using (StreamReader sr = new StreamReader(we.Response.GetResponseStream()))
                        {
                            string body = sr.ReadToEnd();
                            error += body;
                            if (error.Length > 500) error = error.Substring(0, 500) + "...";
                        }
                    }
                    else { error += we.Message; }
                }
                catch { error += we.Message; }
                Log.Warning("Avatar API (Pixazo): " + error);
                return false;
            }
        }

        // =========================================================================
        // Naga.ac API — OpenAI-compatible images/generations
        // Free tier: flux-1-schnell:free, sdxl:free, dall-e-3:free
        // =========================================================================
        private static bool CallNagaAc(
            string apiKey, string model,
            string prompts, string prefix, string suffix,
            string negativePrompt,
            string outputPath, out string error)
        {
            error = null;
            string positive = (prompts + " " + suffix).Trim();
            if (!string.IsNullOrEmpty(prefix))
                positive = prefix + " " + positive;
            while (positive.Contains("  ")) positive = positive.Replace("  ", " ");
            string fullPrompt = positive;
            if (!string.IsNullOrEmpty(negativePrompt))
                fullPrompt += ".\n\nAVOID: " + negativePrompt;
            string requestJson = "{" +
                "\"model\":\"" + JsonEscapeValue(model) + "\"," +
                "\"prompt\":\"" + JsonEscapeValue(fullPrompt) + "\"," +
                "\"size\":\"1024x1024\"," +
                "\"n\":1," +
                "\"response_format\":\"b64_json\"" +
                "}";
            byte[] bodyBytes = Encoding.UTF8.GetBytes(requestJson);
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create("https://api.naga.ac/v1/images/generations");
            req.Method = "POST";
            req.ContentType = "application/json";
            req.Headers["Authorization"] = "Bearer " + apiKey;
            req.Timeout = 180000;
            req.ContentLength = bodyBytes.Length;
            try
            {
                using (Stream reqStream = req.GetRequestStream())
                    reqStream.Write(bodyBytes, 0, bodyBytes.Length);
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    string responseJson;
                    using (StreamReader reader = new StreamReader(resp.GetResponseStream()))
                        responseJson = reader.ReadToEnd();
                    string imageData = ExtractJsonField(responseJson, "data.0.b64_json");
                    if (!string.IsNullOrEmpty(imageData))
                    {
                        File.WriteAllBytes(outputPath, Convert.FromBase64String(imageData));
                        return true;
                    }
                    string imageUrl = ExtractJsonField(responseJson, "data.0.url");
                    if (!string.IsNullOrEmpty(imageUrl))
                    {
                        using (WebClient wc = new WebClient())
                            wc.DownloadFile(imageUrl, outputPath);
                        return true;
                    }
                    error = "Naga.ac: no image in response";
                    return false;
                }
            }
            catch (WebException we)
            {
                error = "Naga.ac: ";
                try
                {
                    if (we.Response != null)
                        using (StreamReader sr = new StreamReader(we.Response.GetResponseStream()))
                            error += sr.ReadToEnd();
                    else error += we.Message;
                }
                catch { error += we.Message; }
                if (error.Length > 500) error = error.Substring(0, 500) + "...";
                return false;
            }
        }

        private static bool TestNagaAcConnection(string apiKey, string model, out string message)
        {
            message = "";
            if (string.IsNullOrEmpty(apiKey)) { message = "API key is empty."; return false; }
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create("https://api.naga.ac/v1/models");
                req.Method = "GET";
                req.Headers["Authorization"] = "Bearer " + apiKey;
                req.Timeout = 15000;
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    message = "Naga.ac API key is valid.";
                    return resp.StatusCode == HttpStatusCode.OK;
                }
            }
            catch (WebException we)
            {
                if (we.Response is HttpWebResponse hwr && hwr.StatusCode == HttpStatusCode.Unauthorized)
                    message = "Invalid API key.";
                else
                    message = "Naga.ac unreachable: " + we.Message;
                return false;
            }
        }

        // =========================================================================
        // Pollinations.ai Image Generation API
        //
        // Text-to-image via GET request. Free tier available (API key optional).
        // Endpoint: GET https://gen.pollinations.ai/image/{prompt}
        // Models: zimage (default), flux, flux-pro, etc.
        // Docs: https://gen.pollinations.ai/docs
        // =========================================================================
        private static bool CallPollinations(
            string apiKey,
            string prompts, string prefix, string suffix,
            string negativePrompt, bool prependPositive,
            string model, string outputPath,
            out string error)
        {
            error = null;

            // Build the positive prompt
            string positive;
            if (prependPositive)
                positive = (prefix + " " + prompts + " " + suffix).Trim();
            else
                positive = (prompts + " " + suffix).Trim();
            while (positive.Contains("  ")) positive = positive.Replace("  ", " ");

            // Append negative prompt as inline instructions
            if (!string.IsNullOrEmpty(negativePrompt))
                positive += ". AVOID: " + negativePrompt;

            string usedModel = string.IsNullOrEmpty(model) ? "zimage" : model;
            string encodedPrompt = Uri.EscapeDataString(positive);
            string url = "https://gen.pollinations.ai/image/" + encodedPrompt +
                         "?width=480&height=576" +
                         "&model=" + Uri.EscapeDataString(usedModel) +
                         "&seed=" + new System.Random().Next(0, 999999).ToString();

            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "GET";
            req.Accept = "image/png,image/jpeg";
            if (!string.IsNullOrEmpty(apiKey))
                req.Headers["Authorization"] = "Bearer " + apiKey;
            req.Timeout = 180000; // 3 minutes

            try
            {
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    if (resp.StatusCode != HttpStatusCode.OK)
                    {
                        error = "Pollinations returned HTTP " + (int)resp.StatusCode;
                        Log.Warning("Avatar API (Pollinations): " + error);
                        return false;
                    }

                    using (Stream responseStream = resp.GetResponseStream())
                    {
                        using (FileStream fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write))
                        {
                            responseStream.CopyTo(fileStream);
                        }
                    }
                    return true;
                }
            }
            catch (WebException we)
            {
                error = "Pollinations request failed: ";
                try
                {
                    if (we.Response != null)
                    {
                        using (StreamReader sr = new StreamReader(we.Response.GetResponseStream()))
                        {
                            string body = sr.ReadToEnd();
                            error += body;
                            if (error.Length > 500) error = error.Substring(0, 500) + "...";
                        }
                    }
                    else
                    {
                        error += we.Message;
                    }
                }
                catch { error += we.Message; }
                Log.Warning("Avatar API (Pollinations): " + error);
                return false;
            }
        }

        // =========================================================================
        // Pollinations Test Connection
        // =========================================================================
        private static bool TestPollinationsConnection(string apiKey, string model, out string message)
        {
            message = "";
            try
            {
                string usedModel = string.IsNullOrEmpty(model) ? "zimage" : model;
                // Hit the docs endpoint to verify the server is reachable
                string url = "https://gen.pollinations.ai/docs";
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET";
                req.Accept = "text/plain";
                if (!string.IsNullOrEmpty(apiKey))
                    req.Headers["Authorization"] = "Bearer " + apiKey;
                req.Timeout = 15000;

                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    message = "Pollinations.ai is reachable (model: " + usedModel + ").";
                    return resp.StatusCode == HttpStatusCode.OK;
                }
            }
            catch (WebException we)
            {
                try
                {
                    if (we.Response != null)
                    {
                        using (StreamReader sr = new StreamReader(we.Response.GetResponseStream()))
                            message = sr.ReadToEnd();
                    }
                    else
                    {
                        message = we.Message;
                    }
                }
                catch { message = we.Message; }
                if (message.Length > 500) message = message.Substring(0, 500) + "...";
                return false;
            }
        }

        // =========================================================================
        // Helpers
        // =========================================================================

        private static void WriteMultipartField(Stream stream, byte[] boundaryBytes,
            byte[] crlf, string name, string value)
        {
            stream.Write(boundaryBytes, 0, boundaryBytes.Length);
            string header = "Content-Disposition: form-data; name=\"" + name + "\"\r\n\r\n";
            byte[] headerBytes = Encoding.UTF8.GetBytes(header);
            stream.Write(headerBytes, 0, headerBytes.Length);
            byte[] valueBytes = Encoding.UTF8.GetBytes(value);
            stream.Write(valueBytes, 0, valueBytes.Length);
            stream.Write(crlf, 0, crlf.Length);
        }

        /// <summary>
        /// Extracts a value from a JSON object by a dotted path.
        /// E.g., "artifacts.0.base64" navigates { artifacts: [{ base64: "..." }] }.
        /// Simple parser — no external JSON library dependency.
        /// </summary>
        private static string ExtractJsonField(string json, string path)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(path)) return null;
            try
            {
                string[] segments = path.Split('.');
                string current = json.Trim();
                foreach (string seg in segments)
                {
                    current = current.Trim();
                    if (current.StartsWith("{"))
                    {
                        // Object: find key "seg"
                        string escapedKey = "\"" + seg + "\"";
                        int keyIdx = FindJsonKey(current, escapedKey);
                        if (keyIdx < 0) return null;
                        current = ExtractJsonValue(current, keyIdx + escapedKey.Length);
                    }
                    else if (current.StartsWith("["))
                    {
                        // Array: index by seg (must be integer)
                        if (!int.TryParse(seg, out int idx)) return null;
                        var elements = SplitJsonArray(current);
                        if (idx < 0 || idx >= elements.Count) return null;
                        current = elements[idx];
                    }
                    else
                    {
                        // Primitive?
                        return current.Trim('"');
                    }
                }
                current = current.Trim();
                if (current.StartsWith("\"") && current.EndsWith("\""))
                    current = current.Substring(1, current.Length - 2);
                return current;
            }
            catch (Exception e)
            {
                Log.Warning("Avatar API: JSON extract failed for path '" + path + "': " + e.Message);
                return null;
            }
        }

        private static int FindJsonKey(string json, string escapedKey)
        {
            int i = 0;
            while (i < json.Length)
            {
                int candidate = json.IndexOf(escapedKey, i, StringComparison.Ordinal);
                if (candidate < 0) return -1;
                // Must be preceded by whitespace, comma, or {
                bool validBefore = false;
                for (int j = candidate - 1; j >= 0; j--)
                {
                    char c = json[j];
                    if (char.IsWhiteSpace(c)) continue;
                    validBefore = (c == ',' || c == '{');
                    break;
                }
                // Must be followed by whitespace and :
                int after = candidate + escapedKey.Length;
                while (after < json.Length && char.IsWhiteSpace(json[after])) after++;
                if (validBefore && after < json.Length && json[after] == ':')
                    return candidate;
                i = candidate + 1;
            }
            return -1;
        }

        private static string ExtractJsonValue(string json, int startIdx)
        {
            // Skip whitespace and the colon
            int i = startIdx;
            while (i < json.Length && (char.IsWhiteSpace(json[i]) || json[i] == ':')) i++;
            if (i >= json.Length) return "";

            if (json[i] == '"')
            {
                // String value
                int end = i + 1;
                while (end < json.Length)
                {
                    if (json[end] == '\\') { end += 2; continue; }
                    if (json[end] == '"') break;
                    end++;
                }
                return json.Substring(i, end - i + 1);
            }
            if (json[i] == '{')
            {
                // Object value
                int depth = 0;
                int start = i;
                while (i < json.Length)
                {
                    if (json[i] == '{') depth++;
                    else if (json[i] == '}') { depth--; if (depth == 0) { i++; break; } }
                    i++;
                }
                return json.Substring(start, i - start);
            }
            if (json[i] == '[')
            {
                // Array value
                int depth = 0;
                int start = i;
                while (i < json.Length)
                {
                    if (json[i] == '[') depth++;
                    else if (json[i] == ']') { depth--; if (depth == 0) { i++; break; } }
                    i++;
                }
                return json.Substring(start, i - start);
            }
            // Primitive (number, true, false, null)
            int valEnd = i;
            while (valEnd < json.Length && !char.IsWhiteSpace(json[valEnd]) && json[valEnd] != ',' && json[valEnd] != '}' && json[valEnd] != ']')
                valEnd++;
            return json.Substring(i, valEnd - i);
        }

        private static System.Collections.Generic.List<string> SplitJsonArray(string json)
        {
            var result = new System.Collections.Generic.List<string>();
            if (!json.StartsWith("[")) return result;
            int i = 1;
            while (i < json.Length)
            {
                while (i < json.Length && (char.IsWhiteSpace(json[i]) || json[i] == ',')) i++;
                if (i >= json.Length || json[i] == ']') break;
                string val = ExtractJsonValue(json, i);
                result.Add(val);
                i += val.Length;
            }
            return result;
        }

        /// <summary>
        /// Escapes a string value for safe embedding in a JSON string.
        /// Used for template placeholder replacement in the generic API.
        /// Does NOT add surrounding quotes — the template already has them.
        /// </summary>
        private static string JsonEscapeValue(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
        }

        // =========================================================================
        // TestConnection — validates API connectivity without generating an image
        // =========================================================================

        public static void TestConnection(
            ApiProvider provider, string apiKey, string endpoint, string model,
            Action<bool, string> onComplete)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                bool success = false;
                string message = "";
                try
                {
                    switch (provider)
                    {
                        case ApiProvider.GoogleGemini:
                            success = TestGeminiConnection(apiKey, model, out message);
                            break;
                        case ApiProvider.OpenRouter:
                            success = TestOpenRouterConnection(apiKey, out message);
                            break;
                        case ApiProvider.Pixazo:
                            success = TestPixazoConnection(apiKey, model, out message);
                            break;
                        case ApiProvider.NagaAc:
                            success = TestNagaAcConnection(apiKey, model, out message);
                            break;
                        case ApiProvider.Pollinations:
                            success = TestPollinationsConnection(apiKey, model, out message);
                            break;
                        case ApiProvider.StabilityAI:
                            success = TestStabilityConnection(apiKey, endpoint, out message);
                            break;
                        case ApiProvider.Generic:
                            success = TestGenericConnection(apiKey, endpoint, out message);
                            break;
                        default:
                            message = "Unknown provider.";
                            break;
                    }
                }
                catch (Exception ex)
                {
                    message = "Exception: " + ex.Message;
                }

                bool capturedSuccess = success;
                string capturedMessage = message;
                LongEventHandler.ExecuteWhenFinished(() => onComplete(capturedSuccess, capturedMessage));
            });
        }

        private static bool TestGeminiConnection(string apiKey, string model, out string message)
        {
            message = "";
            if (string.IsNullOrEmpty(apiKey))
            {
                message = "API key is empty.";
                return false;
            }
            string checkModel = string.IsNullOrEmpty(model) ? "gemini-3.1-flash-lite-image" : model;
            string url = "https://generativelanguage.googleapis.com/v1beta/models/" +
                Uri.EscapeDataString(checkModel) + "?key=" + Uri.EscapeDataString(apiKey);

            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "GET";
            req.Timeout = 15000;

            try
            {
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    if (resp.StatusCode == HttpStatusCode.OK)
                    {
                        message = "Gemini API is reachable. Model '" + checkModel + "' exists.";
                        return true;
                    }
                    message = "Gemini returned HTTP " + (int)resp.StatusCode;
                    return false;
                }
            }
            catch (WebException we)
            {
                try
                {
                    if (we.Response != null)
                    {
                        using (StreamReader sr = new StreamReader(we.Response.GetResponseStream()))
                        {
                            string body = sr.ReadToEnd();
                            if (body.Contains("API_KEY_INVALID") || body.Contains("API key not valid"))
                                message = "Invalid API key.";
                            else if (body.Contains("not found") || body.Contains("404"))
                                message = "Model '" + checkModel + "' not found or not accessible.";
                            else
                                message = "Gemini error: " + (body.Length > 200 ? body.Substring(0, 200) : body);
                        }
                    }
                    else
                    {
                        message = "Network error: " + we.Message;
                    }
                }
                catch { message = we.Message; }
                return false;
            }
        }

        private static bool TestOpenRouterConnection(string apiKey, out string message)
        {
            message = "";
            if (string.IsNullOrEmpty(apiKey))
            {
                message = "API key is empty.";
                return false;
            }

            HttpWebRequest req = (HttpWebRequest)WebRequest.Create("https://openrouter.ai/api/v1/auth/key");
            req.Method = "GET";
            req.Headers["Authorization"] = "Bearer " + apiKey;
            req.Timeout = 15000;

            try
            {
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    if (resp.StatusCode == HttpStatusCode.OK)
                    {
                        message = "OpenRouter API key is valid.";
                        return true;
                    }
                    message = "OpenRouter returned HTTP " + (int)resp.StatusCode;
                    return false;
                }
            }
            catch (WebException we)
            {
                try
                {
                    if (we.Response is HttpWebResponse hwr && hwr.StatusCode == HttpStatusCode.Unauthorized)
                        message = "Invalid API key (unauthorized).";
                    else if (we.Response != null)
                    {
                        using (StreamReader sr = new StreamReader(we.Response.GetResponseStream()))
                        {
                            string body = sr.ReadToEnd();
                            message = "OpenRouter error: " + (body.Length > 200 ? body.Substring(0, 200) : body);
                        }
                    }
                    else
                        message = "Network error: " + we.Message;
                }
                catch { message = we.Message; }
                return false;
            }
        }

        private static bool TestPixazoConnection(string apiKey, string model, out string message)
        {
            message = "";
            if (string.IsNullOrEmpty(apiKey))
            {
                message = "API key is empty.";
                return false;
            }

            string pixModel = string.IsNullOrEmpty(model) ? "sdxl-base" : model;
            string endpoint;
            string requestJson;
            string modelLower = pixModel.ToLowerInvariant();

            if (modelLower.Contains("inpaint"))
            {
                endpoint = "https://gateway.pixazo.ai/inpainting/v1/getImage";
                requestJson = "{\"prompt\":\"test\",\"height\":64,\"width\":64,\"num_steps\":1,\"guidance\":1}";
            }
            else
            {
                endpoint = "https://gateway.pixazo.ai/getImage/v1/getSDXLImage";
                requestJson = "{\"prompt\":\"test\",\"height\":64,\"width\":64,\"num_steps\":1,\"guidance_scale\":1}";
            }

            byte[] bodyBytes = Encoding.UTF8.GetBytes(requestJson);

            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(endpoint);
            req.Method = "POST";
            req.ContentType = "application/json";
            req.Headers["Ocp-Apim-Subscription-Key"] = apiKey;
            req.Timeout = 15000;
            req.ContentLength = bodyBytes.Length;

            try
            {
                using (Stream reqStream = req.GetRequestStream())
                {
                    reqStream.Write(bodyBytes, 0, bodyBytes.Length);
                }
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    message = "Pixazo API is reachable. Model '" + pixModel + "' is available.";
                    return true;
                }
            }
            catch (WebException we)
            {
                try
                {
                    if (we.Response is HttpWebResponse hwr &&
                        (hwr.StatusCode == HttpStatusCode.Unauthorized || hwr.StatusCode == HttpStatusCode.Forbidden))
                        message = "Invalid API key (unauthorized).";
                    else if (we.Response != null)
                    {
                        using (StreamReader sr = new StreamReader(we.Response.GetResponseStream()))
                        {
                            string body = sr.ReadToEnd();
                            message = "Pixazo error: " + (body.Length > 200 ? body.Substring(0, 200) : body);
                        }
                    }
                    else
                        message = "Network error: " + we.Message;
                }
                catch { message = we.Message; }
                return false;
            }
        }

        private static bool TestStabilityConnection(string apiKey, string endpoint, out string message)
        {
            message = "";
            if (string.IsNullOrEmpty(apiKey))
            {
                message = "API key is empty.";
                return false;
            }

            string url = string.IsNullOrEmpty(endpoint)
                ? "https://api.stability.ai/v1/user/account"
                : endpoint;

            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "GET";
            req.Headers["Authorization"] = "Bearer " + apiKey;
            req.Accept = "application/json";
            req.Timeout = 15000;

            try
            {
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    if (resp.StatusCode == HttpStatusCode.OK)
                    {
                        message = "Stability AI API is reachable.";
                        return true;
                    }
                    message = "Stability AI returned HTTP " + (int)resp.StatusCode;
                    return false;
                }
            }
            catch (WebException we)
            {
                try
                {
                    if (we.Response is HttpWebResponse hwr && hwr.StatusCode == HttpStatusCode.Unauthorized)
                        message = "Invalid API key (unauthorized).";
                    else if (we.Response != null)
                    {
                        using (StreamReader sr = new StreamReader(we.Response.GetResponseStream()))
                        {
                            string body = sr.ReadToEnd();
                            message = "Stability AI error: " + (body.Length > 200 ? body.Substring(0, 200) : body);
                        }
                    }
                    else
                        message = "Network error: " + we.Message;
                }
                catch { message = we.Message; }
                return false;
            }
        }

        private static bool TestGenericConnection(string apiKey, string endpoint, out string message)
        {
            message = "";
            if (string.IsNullOrEmpty(endpoint))
            {
                message = "No endpoint URL configured.";
                return false;
            }

            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(endpoint);
            req.Method = "GET";
            if (!string.IsNullOrEmpty(apiKey))
                req.Headers["Authorization"] = "Bearer " + apiKey;
            req.Timeout = 15000;

            try
            {
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    message = "Endpoint is reachable (HTTP " + (int)resp.StatusCode + ").";
                    return resp.StatusCode == HttpStatusCode.OK || resp.StatusCode == HttpStatusCode.Accepted;
                }
            }
            catch (WebException we)
            {
                try
                {
                    if (we.Response is HttpWebResponse hwr && hwr.StatusCode == HttpStatusCode.Unauthorized)
                        message = "Invalid API key (unauthorized).";
                    else if (we.Response != null)
                        message = "Endpoint reachable but returned HTTP " + (int)((HttpWebResponse)we.Response).StatusCode + ".";
                    else
                        message = "Cannot reach endpoint: " + we.Message;
                }
                catch { message = "Cannot reach endpoint: " + we.Message; }
                return false;
            }
        }
    }
}

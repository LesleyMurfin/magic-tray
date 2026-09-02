// SPDX-License-Identifier: MIT
using System;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace MagicMouseTray;

internal static class UpdateChecker
{
    static readonly HttpClient _client = new();

    public static async Task<string?> CheckForUpdateAsync()
    {
        try
        {
            var asmVer = Assembly.GetExecutingAssembly().GetName().Version;
            var semver = asmVer != null ? $"{asmVer.Major}.{asmVer.Minor}.{asmVer.Build}" : "1.0.0";
            
            var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/LesleyMurfin/magic-tray/releases/latest");
            request.Headers.UserAgent.ParseAdd($"MagicTray/{semver}");
            
            var response = await _client.SendAsync(request);
            response.EnsureSuccessStatusCode();
            
            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("tag_name", out var tagElem))
            {
                var tag = tagElem.GetString();
                if (!string.IsNullOrEmpty(tag))
                {
                    var match = Regex.Match(tag, @"v?(\d+\.\d+\.\d+)");
                    if (match.Success && Version.TryParse(match.Groups[1].Value, out var releaseVer))
                    {
                        if (asmVer != null && releaseVer > asmVer)
                        {
                            return tag;
                        }
                        return null; // Not newer
                    }
                    else
                    {
                        Logger.Log($"UPDATE_CHECK_FAILED tag_name={tag}");
                        return null;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"UPDATE_CHECK_FAILED err=\"{ex.Message}\"");
        }
        
        return null;
    }
}

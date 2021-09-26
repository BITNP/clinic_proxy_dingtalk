using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace ClinicProxyTest.Controllers;

[ApiController]
[Route("[controller]")]
public class UserController : ControllerBase
{
    private static readonly Guid AccessTokenKey = new();
    private readonly ILogger<UserController> _logger;
    private readonly IHttpClientFactory _clientFactory;
    private readonly IConfiguration _configuration;
    private readonly IMemoryCache _cache;
    private readonly string getAccessTokenApi = "https://oapi.dingtalk.com/gettoken";
    private readonly string getUserInfoApi = "https://oapi.dingtalk.com/topapi/v2/user/getuserinfo";
    private readonly string getUserDetailsApi = "https://oapi.dingtalk.com/topapi/v2/user/get";
    private readonly string appkey;
    private readonly string appsecret;
    private readonly MemoryCacheEntryOptions MemoryCacheEntryOptions = new() { SlidingExpiration = TimeSpan.FromHours(2) };

    public UserController(ILogger<UserController> logger, IHttpClientFactory clientFactory,
        IMemoryCache memoryCache, IConfiguration configuration)
    {
        _logger = logger;
        _clientFactory = clientFactory;
        _cache = memoryCache;
        _configuration = configuration;
        appkey = _configuration["appkey"];
        appsecret = _configuration["appsecret"];
    }
    private async Task<string> RequestAccessTokenAsync()
    {
        var client = _clientFactory.CreateClient();
        Uri requestUri = new(getAccessTokenApi + $"?appkey={appkey}&appsecret={appsecret}");
        var response = await client.GetAsync(requestUri);
        var responseText = await response.Content.ReadAsStringAsync();
        try
        {
            var responseJson = JsonDocument.Parse(responseText);
            int errcode = responseJson.RootElement.GetProperty("errcode").GetInt32();
            if (errcode == 0)
            {
                return responseJson.RootElement.GetProperty("access_token").GetString() ?? string.Empty;
            }
            else
            {
                string? errmsg = responseJson.RootElement.GetProperty("errmsg").GetString();
                _logger.LogWarning("Failed to get access_token: {errmsg}", errmsg ?? "null errmsg");
            }
        }
        catch (Exception e)
        {
            _logger.LogError("Exception while getting access_token: {e}", e.Message);
        }
        return string.Empty;
    }
    private async Task<string> RequestUserIdAsync(string accessToken, string authCode)
    {
        var client = _clientFactory.CreateClient();
        Uri requestUri = new(getUserInfoApi + $"?access_token={accessToken}");
        HttpContent httpContent = new StringContent($"{{\"code\":\"{authCode}\"}}");
        var response = await client.PostAsync(requestUri, httpContent);
        var responseText = await response.Content.ReadAsStringAsync();
        try
        {
            var responseJson = JsonDocument.Parse(responseText);
            int errcode = responseJson.RootElement.GetProperty("errcode").GetInt32();
            if (errcode == 0)
            {
                return responseJson.RootElement.GetProperty("result")
                        .GetProperty("userid").GetString() ?? string.Empty;
            }
            else
            {
                string? errmsg = responseJson.RootElement.GetProperty("errmsg").GetString();
                _logger.LogWarning("Failed to get user info: {errmsg}", errmsg ?? "null errmsg");
            }
        }
        catch (Exception e)
        {
            _logger.LogError("Exception while getting user info: {e}", e.Message);
        }
        return string.Empty;
    }
    private async Task<string> RequestUserJobNumberAsync(string accessToken, string userid)
    {
        var client = _clientFactory.CreateClient();
        Uri requestUri = new(getUserDetailsApi + $"?access_token={accessToken}");
        HttpContent httpContent = new StringContent($"{{\"userid\":\"{userid}\"}}");
        var response = await client.PostAsync(requestUri, httpContent);
        var responseText = await response.Content.ReadAsStringAsync();
        try
        {
            var responseJson = JsonDocument.Parse(responseText);
            int errcode = responseJson.RootElement.GetProperty("errcode").GetInt32();
            if (errcode == 0)
            {
                return responseJson.RootElement.GetProperty("result")
                            .GetProperty("job_number").GetString() ?? string.Empty;
            }
            else
            {
                string? errmsg = responseJson.RootElement.GetProperty("errmsg").GetString();
                _logger.LogWarning("Failed to get user details: {errmsg}", errmsg ?? "null errmsg");
            }
        }
        catch (Exception e)
        {
            _logger.LogError("Exception while getting user details: {e}", e.Message);
        }
        return string.Empty;
    }
    [HttpGet]
    public IActionResult OnGet()
    {
        var savedToken = Request.Headers["user-token"].ToString();
        if (savedToken.Length == 0 || !_cache.TryGetValue(savedToken, out string s))
        {
            return Ok(new { status = 403 });
        }
        else
        {
            _logger.LogInformation("Cached job number {n} for {code}", s, savedToken);
            return Ok(new { status = 200 });
        }
    }

    [HttpPost]
    public async Task<IActionResult> OnPostAsync()
    {
        string authCode = Request.Headers["user-token"].ToString();
        if (authCode.Length == 0)
        {
            return BadRequest();
        }
        if (_cache.TryGetValue(authCode, out _))
        {
            return Ok(new { status = 200 });
        }
        if (!_cache.TryGetValue(AccessTokenKey, out string accessToken))
        {
            accessToken = await RequestAccessTokenAsync();
            if (accessToken == string.Empty)
            {
                goto getJobNumberFailed;
            }
            _cache.Set(AccessTokenKey, accessToken, 
                new MemoryCacheEntryOptions() { AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1.75) });
        }
        string userId = await RequestUserIdAsync(accessToken, authCode);
        if (userId == string.Empty)
        {
            goto getJobNumberFailed;
        }
        string userJobNumber = await RequestUserJobNumberAsync(accessToken, userId);
        if (userId == string.Empty)
        {
            goto getJobNumberFailed;
        }
        _cache.Set(authCode, userJobNumber, MemoryCacheEntryOptions);
        _logger.LogInformation("Got job number {n} for authcode {code}", userJobNumber, authCode);
        return Ok(new { status = 200, student_id = userJobNumber });

    getJobNumberFailed:
        _logger.LogWarning("Failed to get job number for authcode {code}", authCode);
        return Ok(new { status = 500 });
    }
}

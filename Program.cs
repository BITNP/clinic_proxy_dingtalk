using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

var builder = WebApplication. CreateSlimBuilder(args);

builder.Services.AddHttpClient();
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders =
        ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (builder.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}
app.UseForwardedHeaders();

app.UseStaticFiles();
app.UseFileServer(new FileServerOptions
{
    RequestPath = "/lightapp"
});

var getAccessTokenApi = "https://oapi.dingtalk.com/gettoken";
var getUserInfoApi = "https://oapi.dingtalk.com/topapi/v2/user/getuserinfo";
var getUserDetailsApi = "https://oapi.dingtalk.com/topapi/v2/user/get";
var AccessTokenKey = new Guid();
var _cache = new MemoryCache(new MemoryCacheOptions { });
var _clientFactory = app.Services.GetService<IHttpClientFactory>();
var _logger = app.Logger;
var appkey = app.Configuration["appkey"];
var appsecret = app.Configuration["appsecret"];
var _host = app.Configuration["backend"];
var _apikey = app.Configuration["apikey"];

app.MapGet("/", async context => {
    await context.Response.WriteAsync("BITNP clinic proxy for i-bit / dingtalk.");
});

app.MapGet("/user", async context =>
{
    var savedToken = context.Request.Headers["user-token"].ToString();
    context.Response.ContentType = "application/json";
    if (savedToken.Length == 0 || !_cache.TryGetValue(savedToken, out string s))
    {
        await context.Response.WriteAsync("{\"status\":403}");
    }
    else
    {
        await context.Response.WriteAsync("{\"status\":200}");
    }
});
app.MapPost("/user", async context =>
{
    string authCode = context.Request.Headers["user-token"].ToString();
    if (authCode.Length == 0)
    {
        context.Response.StatusCode = 400;
        return;
    }

    context.Response.ContentType = "application/json";
    if (_cache.TryGetValue(authCode, out _))
    {
        await context.Response.WriteAsync("{\"status\":200}");
        return;
    }

    string userId, userJobNumber;
    try
    {
        if (!_cache.TryGetValue(AccessTokenKey, out string accessToken))
        {
            accessToken = await RequestAccessTokenAsync();
            _cache.Set(AccessTokenKey, accessToken,
                new MemoryCacheEntryOptions() { AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1.75) });
        }
        userId = await RequestUserIdAsync(accessToken, authCode);
        userJobNumber = await RequestUserJobNumberAsync(accessToken, userId);
    }
    catch (Exception e)
    {
        _logger.LogWarning("Failed to get job number for authcode {code}, Exception {e}", authCode, e.ToString());
        await context.Response.WriteAsync("{\"status\":500}");
        return;
    }

    _ = _cache.Set(authCode, userJobNumber, new MemoryCacheEntryOptions() { SlidingExpiration = TimeSpan.FromHours(2) });
    await context.Response.WriteAsync($"{{\"status\":200,\"student_id\":{userJobNumber}}}");
});
app.MapGet("/proxy", async context =>
{
    var remoteAddr = context.Connection.RemoteIpAddress?.ToString() ?? "0.0.0.0";
    var url = context.Request.Headers["url"].ToString();
    var savedToken = context.Request.Headers["user-token"].ToString();
    string cachedUserName;
    if (url.Length == 0 || savedToken.Length == 0)
    {
        context.Response.StatusCode = 400;
        return;
    }
    else if (!_cache.TryGetValue(savedToken, out cachedUserName))
    {
        _logger.LogWarning("Reject request {m} {url}", context.Request.Method, url);
        context.Response.StatusCode = 403;
        return;
    }
    var client = _clientFactory.CreateClient();
    HttpRequestMessage request = new();
    string requestUrl = _host + url;
    request.RequestUri = new Uri($"{requestUrl}?username={cachedUserName}");
    var requestContentType = context.Request.ContentType ?? "text/plain";
    request.Method = HttpMethod.Get;
    string timeStr = DateTime.Now.ToString("R");
    string apikey = GetMd5Hash($"{_apikey}{cachedUserName}{timeStr}");
    request.Headers.Add("Accept", context.Request.Headers.Accept.ToString());
    request.Headers.Add("X-API-KEY", apikey);
    request.Headers.Add("Date", timeStr);
    request.Headers.Add("X-Forwarded-For", remoteAddr);
    var response = await client.SendAsync(request);
    var content = await response.Content.ReadAsStringAsync();
    var responseType = response.Content.Headers.ContentType;

    context.Response.StatusCode = (int)response.StatusCode;
    context.Response.ContentType = responseType?.ToString() ?? "text/plain";
    if (content != string.Empty)
    {
        await context.Response.WriteAsync(content);
    }
});
app.MapPost("/proxy", async context =>
{
    var remoteAddr = context.Connection.RemoteIpAddress?.ToString() ?? "0.0.0.0";
    var url = context.Request.Headers["url"].ToString();
    var savedToken = context.Request.Headers["user-token"].ToString();
    string cachedUserName;
    if (url.Length == 0 || savedToken.Length == 0)
    {
        context.Response.StatusCode = 400;
        return;
    }
    else if (!_cache.TryGetValue(savedToken, out cachedUserName))
    {
        _logger.LogWarning("Reject request {m} {url}", context.Request.Method, url);
        context.Response.StatusCode = 403;
        return;
    }
    var client = _clientFactory.CreateClient();
    HttpRequestMessage request = new();
    string requestUrl = _host + url;
    request.RequestUri = new Uri($"{requestUrl}?username={cachedUserName}");
    var requestContentType = context.Request.ContentType ?? "text/plain";
    request.Method = HttpMethod.Post;
    request.Content = await JsonRequestBodyToStringContent(context.Request.Body, requestContentType, cachedUserName);
    string timeStr = DateTime.Now.ToString("R");
    string apikey = GetMd5Hash($"{_apikey}{cachedUserName}{timeStr}");
    request.Headers.Add("Accept", context.Request.Headers.Accept.ToString());
    request.Headers.Add("X-API-KEY", apikey);
    request.Headers.Add("Date", timeStr);
    request.Headers.Add("X-Forwarded-For", remoteAddr);
    var response = await client.SendAsync(request);
    var content = await response.Content.ReadAsStringAsync();
    var responseType = response.Content.Headers.ContentType;

    context.Response.StatusCode = (int)response.StatusCode;
    context.Response.ContentType = responseType?.ToString() ?? "text/plain";
    if (content != string.Empty)
    {
        await context.Response.WriteAsync(content);
    }
});
app.MapPut("/proxy", async context =>
{
    var remoteAddr = context.Connection.RemoteIpAddress?.ToString() ?? "0.0.0.0";
    var url = context.Request.Headers["url"].ToString();
    var savedToken = context.Request.Headers["user-token"].ToString();
    string cachedUserName;
    if (url.Length == 0 || savedToken.Length == 0)
    {
        context.Response.StatusCode = 400;
        return;
    }
    else if (!_cache.TryGetValue(savedToken, out cachedUserName))
    {
        _logger.LogWarning("Reject request {m} {url}", context.Request.Method, url);
        context.Response.StatusCode = 403;
        return;
    }
    var client = _clientFactory.CreateClient();
    HttpRequestMessage request = new();
    string requestUrl = _host + url;
    request.RequestUri = new Uri($"{requestUrl}?username={cachedUserName}");
    var requestContentType = context.Request.ContentType ?? "text/plain";
    request.Method = HttpMethod.Put;
    request.Content = await JsonRequestBodyToStringContent(context.Request.Body, requestContentType, cachedUserName);
    string timeStr = DateTime.Now.ToString("R");
    string apikey = GetMd5Hash($"{_apikey}{cachedUserName}{timeStr}");
    request.Headers.Add("Accept", context.Request.Headers.Accept.ToString());
    request.Headers.Add("X-API-KEY", apikey);
    request.Headers.Add("Date", timeStr);
    request.Headers.Add("X-Forwarded-For", remoteAddr);
    var response = await client.SendAsync(request);
    var content = await response.Content.ReadAsStringAsync();
    var responseType = response.Content.Headers.ContentType;

    context.Response.StatusCode = (int)response.StatusCode;
    context.Response.ContentType = responseType?.ToString() ?? "text/plain";
    if (content != string.Empty)
    {
        await context.Response.WriteAsync(content);
    }
});
app.MapDelete("/proxy", async context =>
{
    var remoteAddr = context.Connection.RemoteIpAddress?.ToString() ?? "0.0.0.0";
    var url = context.Request.Headers["url"].ToString();
    var savedToken = context.Request.Headers["user-token"].ToString();
    string cachedUserName;
    if (url.Length == 0 || savedToken.Length == 0)
    {
        context.Response.StatusCode = 400;
        return;
    }
    else if (!_cache.TryGetValue(savedToken, out cachedUserName))
    {
        _logger.LogWarning("Reject request {m} {url}", context.Request.Method, url);
        context.Response.StatusCode = 403;
        return;
    }
    var client = _clientFactory.CreateClient();
    HttpRequestMessage request = new();
    string requestUrl = _host + url;
    request.RequestUri = new Uri($"{requestUrl}?username={cachedUserName}");
    var requestContentType = context.Request.ContentType ?? "text/plain";
    request.Method = HttpMethod.Delete;
    string timeStr = DateTime.Now.ToString("R");
    string apikey = GetMd5Hash($"{_apikey}{cachedUserName}{timeStr}");
    request.Headers.Add("Accept", context.Request.Headers.Accept.ToString());
    request.Headers.Add("X-API-KEY", apikey);
    request.Headers.Add("Date", timeStr);
    request.Headers.Add("X-Forwarded-For", remoteAddr);
    var response = await client.SendAsync(request);
    var content = await response.Content.ReadAsStringAsync();
    var responseType = response.Content.Headers.ContentType;

    context.Response.StatusCode = (int)response.StatusCode;
    context.Response.ContentType = responseType?.ToString() ?? "text/plain";
    if (content != string.Empty)
    {
        await context.Response.WriteAsync(content);
    }
});

app.Run();



async Task<string> RequestAccessTokenAsync()
{
    var client = _clientFactory.CreateClient();
    Uri requestUri = new(getAccessTokenApi + $"?appkey={appkey}&appsecret={appsecret}");
    var response = await client.GetAsync(requestUri);
    var responseText = await response.Content.ReadAsStringAsync();
    var responseJson = JsonDocument.Parse(responseText);
    int errcode = responseJson.RootElement.GetProperty("errcode").GetInt32();
    if (errcode == 0)
    {
        return responseJson.RootElement.GetProperty("access_token").GetString();
    }
    else
    {
        string errmsg = responseJson.RootElement.GetProperty("errmsg").GetString();
        throw new Exception($"Failed to get access_token: {errmsg}");
    }
}
async Task<string> RequestUserIdAsync(string accessToken, string authCode)
{
    var client = _clientFactory.CreateClient();
    Uri requestUri = new(getUserInfoApi + $"?access_token={accessToken}");
    HttpContent httpContent = new StringContent($"{{\"code\":\"{authCode}\"}}");
    var response = await client.PostAsync(requestUri, httpContent);
    var responseText = await response.Content.ReadAsStringAsync();
    var responseJson = JsonDocument.Parse(responseText);
    int errcode = responseJson.RootElement.GetProperty("errcode").GetInt32();
    if (errcode == 0)
    {
        return responseJson.RootElement.GetProperty("result")
                .GetProperty("userid").GetString() ?? string.Empty;
    }
    else
    {
        string errmsg = responseJson.RootElement.GetProperty("errmsg").GetString();
        throw new Exception($"Failed to get user info: {errmsg}");
    }
}
async Task<string> RequestUserJobNumberAsync(string accessToken, string userid)
{
    var client = _clientFactory.CreateClient();
    Uri requestUri = new(getUserDetailsApi + $"?access_token={accessToken}");
    HttpContent httpContent = new StringContent($"{{\"userid\":\"{userid}\"}}");
    var response = await client.PostAsync(requestUri, httpContent);
    var responseText = await response.Content.ReadAsStringAsync();
    var responseJson = JsonDocument.Parse(responseText);
    int errcode = responseJson.RootElement.GetProperty("errcode").GetInt32();
    if (errcode == 0)
    {
        return responseJson.RootElement.GetProperty("result")
                    .GetProperty("job_number").GetString() ?? string.Empty;
    }
    else
    {
        string errmsg = responseJson.RootElement.GetProperty("errmsg").GetString();
        throw new Exception($"Failed to get user details: {errmsg}");
    }
}


string GetMd5Hash(string input)
{
    byte[] data = MD5.HashData(Encoding.UTF8.GetBytes(input));
    StringBuilder sBuilder = new();
    for (int i = 0; i < data.Length; i++)
    {
        sBuilder.Append(data[i].ToString("x2"));
    }
    return sBuilder.ToString();
}
async Task<StringContent> JsonRequestBodyToStringContent(Stream body,
    string requestContentType, string username)
{
    var bodyString = await new StreamReader(body).ReadToEndAsync();
    var jsonRequestBody = JsonNode.Parse(bodyString);
    if (jsonRequestBody != null)
    {
        if (!jsonRequestBody.AsObject().TryGetPropertyValue("user", out _))
        {
            jsonRequestBody.AsObject().Add("user", username);
        }
        var content = new StringContent(jsonRequestBody.ToJsonString(), Encoding.UTF8);
        content.Headers.Remove("Content-Type");
        content.Headers.Add("Content-Type", requestContentType);
        return content;
    }
    else
    {
        return new StringContent("{}", Encoding.UTF8);
    }
}

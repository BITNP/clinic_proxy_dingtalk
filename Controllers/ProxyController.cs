using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace ClinicProxyTest.Controllers;

[ApiController]
[Route("[controller]")]
public class ProxyController : ControllerBase
{
    private readonly string _host;
    private readonly string _apikey;
    private readonly ILogger<ProxyController> _logger;
    private readonly IHttpClientFactory _clientFactory;
    private readonly IMemoryCache _cache;
    private readonly IConfiguration _configuration;

    public ProxyController(ILogger<ProxyController> logger, IHttpClientFactory clientFactory,
        IMemoryCache memoryCache, IConfiguration configuration)
    {
        _logger = logger;
        _clientFactory = clientFactory;
        _cache = memoryCache;
        _configuration = configuration;
        _host = _configuration["backend"];
        _apikey = _configuration["apikey"];
    }
    static string GetMd5Hash(string input)
    {
        MD5 md5Hash = MD5.Create();
        byte[] data = md5Hash.ComputeHash(Encoding.UTF8.GetBytes(input));
        StringBuilder sBuilder = new();
        for (int i = 0; i < data.Length; i++)
        {
            sBuilder.Append(data[i].ToString("x2"));
        }
        return sBuilder.ToString();
    }
    private async Task<StringContent> JsonRequestBodyToStringContent(Stream body, 
        string requestContentType, string username)
    {
        try
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
        }
        catch (Exception e)
        {
            _logger.LogError("Exception \"{e}\" when handling request body", e.Message);
        }
        return new StringContent(string.Empty);
    }
    [HttpGet]
    [HttpPost]
    [HttpPut]
    [HttpDelete]
    public async Task OnRequestAsync()
    {
        var remoteAddr = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "0.0.0.0";
        var url = Request.Headers["url"].ToString();
        var savedToken = Request.Headers["user-token"].ToString();
        string cachedUserName;
        if (url.Length == 0 || savedToken.Length == 0)
        {
            Response.StatusCode = 400;
            return;
        }
        else if (!_cache.TryGetValue(savedToken, out cachedUserName))
        {
            _logger.LogWarning("Reject request {m} {url}", HttpContext.Request.Method, url);
            Response.StatusCode = 403;
            return;
        }
        _logger.LogInformation("Proxy request {m} {url} for {user}", HttpContext.Request.Method, url, cachedUserName);
        var client = _clientFactory.CreateClient();
        HttpRequestMessage request = new();
        string requestUrl = _host + url;
        request.RequestUri = new Uri($"{requestUrl}?username={cachedUserName}");
        var requestContentType = Request.ContentType ?? "text/plain";
        switch (HttpContext.Request.Method)
        {
            case "GET":
                request.Method = HttpMethod.Get;
                break;
            case "POST":
                request.Method = HttpMethod.Post;
                request.Content = await JsonRequestBodyToStringContent(Request.Body, requestContentType, cachedUserName);
                break;
            case "DELETE":
                request.Method = HttpMethod.Delete;
                break;
            case "PUT":
                request.Method = HttpMethod.Put;
                request.Content = await JsonRequestBodyToStringContent(Request.Body, requestContentType, cachedUserName);
                break;
            default:
                Response.StatusCode = 400;
                return;
        }
        string timeStr = DateTime.Now.ToString("R");
        string apikey = GetMd5Hash($"{_apikey}{cachedUserName}{timeStr}");
        request.Headers.Add("Accept", Request.Headers.Accept.ToString());
        request.Headers.Add("X-API-KEY", apikey);
        request.Headers.Add("Date", timeStr);
        request.Headers.Add("X-Forwarded-For", remoteAddr);
        try
        {
            var response = await client.SendAsync(request);
            var content = await response.Content.ReadAsStringAsync();
            var responseType = response.Content.Headers.ContentType;

            Response.StatusCode = (int)response.StatusCode;
            Response.ContentType = responseType?.ToString() ?? "text/plain";
            if (content != string.Empty)
            {
                await Response.WriteAsync(content);
            }
        }
        catch (HttpRequestException e)
        {
            Response.StatusCode = 500;
            _logger.LogError("Proxy request {m} {url} for {user} failed. Exception {e} ",
                HttpContext.Request.Method, url, cachedUserName, e.Message);
        }
    }
}

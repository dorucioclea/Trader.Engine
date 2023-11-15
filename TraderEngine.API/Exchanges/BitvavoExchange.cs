using AutoMapper;
using Microsoft.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TraderEngine.API.DTOs.Bitvavo.Response;
using TraderEngine.Common.DTOs.API.Request;
using TraderEngine.Common.DTOs.API.Response;
using TraderEngine.Common.Enums;
using TraderEngine.Common.Models;

namespace TraderEngine.API.Exchanges;

public class BitvavoExchange : IExchange
{
  private readonly ILogger<BitvavoExchange> _logger;
  private readonly IMapper _mapper;
  private readonly HttpClient _httpClient;

  public string QuoteSymbol { get; } = "EUR";

  public decimal MinOrderSizeInQuote { get; } = 5;

  public decimal MakerFee { get; } = .0015m;

  public decimal TakerFee { get; } = .0025m;

  public string ApiKey { get; set; } = string.Empty;

  public string ApiSecret { get; set; } = string.Empty;

  public BitvavoExchange(
    ILogger<BitvavoExchange> logger,
    IMapper mapper,
    HttpClient httpClient)
  {
    _logger = logger;
    _mapper = mapper;

    _httpClient = httpClient;
    _httpClient.BaseAddress = new("https://api.bitvavo.com/v2/");
  }

  private string CreateSignature(long timestamp, string method, string url, object? body)
  {
    var inputStrBuilder = new StringBuilder();

    inputStrBuilder.Append(timestamp).Append(method).Append(url);

    if (body != null)
    {
      string bodyJson = JsonSerializer.Serialize(body);

      inputStrBuilder.Append(bodyJson);
    }

    using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(ApiSecret));

    byte[] inputBytes = Encoding.UTF8.GetBytes(inputStrBuilder.ToString());

    byte[] signatureBytes = hmac.ComputeHash(inputBytes);

    var outputStrBuilder = new StringBuilder(signatureBytes.Length * 2);

    foreach (byte b in signatureBytes)
    {
      outputStrBuilder.Append(b.ToString("x2"));
    }

    return outputStrBuilder.ToString();
  }

  private HttpRequestMessage CreateRequestMsg(HttpMethod method, string requestPath, object? body = null)
  {
    var request = new HttpRequestMessage(method, new Uri(_httpClient.BaseAddress!, requestPath));

    request.Headers.Add(HeaderNames.Accept, "application/json");
    request.Headers.Add("bitvavo-access-window", "60000 ");

    long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    string signature = CreateSignature(timestamp, request.Method.ToString(), request.RequestUri!.PathAndQuery, body);

    request.Headers.Add("bitvavo-access-key", ApiKey);
    request.Headers.Add("bitvavo-access-timestamp", timestamp.ToString());
    request.Headers.Add("bitvavo-access-signature", signature);

    return request;
  }

  public async Task<Balance?> GetBalance()
  {
    using var request = CreateRequestMsg(HttpMethod.Get, "balance");

    using var response = await _httpClient.SendAsync(request);

    if (!response.IsSuccessStatusCode)
    {
      _logger.LogError("{url} returned {code} {reason} : {response}",
        request.RequestUri, (int)response.StatusCode, response.ReasonPhrase, await response.Content.ReadAsStringAsync());

      return null;
    }

    var result = await response.Content.ReadFromJsonAsync<List<BitvavoAllocationDto>>();

    if (null == result)
    {
      // TODO: Handle.
      throw new Exception("Failed to deserialize response.");
    }

    var balance = new Balance(QuoteSymbol);

    IEnumerable<Task<Allocation>> priceTasks =
      result

      // Filter out assets of which the amount is 0.
      .Select(allocationDto => (dto: allocationDto, amount: decimal.Parse(allocationDto.Available) + decimal.Parse(allocationDto.InOrder)))
      .Where(alloc => alloc.amount > 0)

      // Get price of each asset.
      .Select(async alloc =>
      {
        var market = new MarketReqDto(QuoteSymbol, alloc.dto.Symbol);

        decimal? price = market.BaseSymbol == QuoteSymbol ? 1 : await GetPrice(market);

        if (null == price)
        {
          // TODO: DO NOT THROW, BUT RETURN NULL AS BALANCE !!
          throw new Exception($"Failed to get price of {market.BaseSymbol}-{market.QuoteSymbol}.");
        }

        var allocation = new Allocation(market, price, alloc.amount);

        return allocation;
      });

    // TODO: Error handling.
    Allocation[] allocations = await Task.WhenAll(priceTasks);

    foreach (var allocation in allocations)
    {
      balance.AddAllocation(allocation);
    }

    return balance;
  }

  public async Task<decimal?> TotalDeposited()
  {
    using var request = CreateRequestMsg(
      HttpMethod.Get, $"depositHistory?symbol={QuoteSymbol}&start=0");

    using var response = await _httpClient.SendAsync(request);

    if (!response.IsSuccessStatusCode)
    {
      _logger.LogError("{url} returned {code} {reason} : {response}",
        request.RequestUri, (int)response.StatusCode, response.ReasonPhrase, await response.Content.ReadAsStringAsync());

      return null;
    }

    var result = await response.Content.ReadFromJsonAsync<JsonArray>();

    if (null == result)
    {
      // TODO: Handle.
      throw new Exception("Failed to deserialize response.");
    }

    return result.Sum(obj => decimal.Parse(obj!["amount"]!.ToString()));
  }

  public async Task<decimal?> TotalWithdrawn()
  {
    using var request = CreateRequestMsg(
      HttpMethod.Get, $"withdrawalHistory?symbol={QuoteSymbol}&start=0");

    using var response = await _httpClient.SendAsync(request);

    if (!response.IsSuccessStatusCode)
    {
      _logger.LogError("{url} returned {code} {reason} : {response}",
        request.RequestUri, (int)response.StatusCode, response.ReasonPhrase, await response.Content.ReadAsStringAsync());

      return null;
    }

    var result = await response.Content.ReadFromJsonAsync<JsonArray>();

    if (null == result)
    {
      // TODO: Handle.
      throw new Exception("Failed to deserialize response.");
    }

    return result.Sum(obj => decimal.Parse(obj!["amount"]!.ToString()));
  }

  public Task<object?> GetCandles(MarketReqDto market, CandleInterval interval, int limit)
  {
    throw new NotImplementedException();
  }

  public async Task<MarketDataDto?> GetMarket(MarketReqDto market)
  {
    using var request = CreateRequestMsg(
      HttpMethod.Get, $"markets?market={market.BaseSymbol.ToUpper()}-{market.QuoteSymbol.ToUpper()}");

    using var response = await _httpClient.SendAsync(request);

    if (!response.IsSuccessStatusCode)
    {
      var error = await response.Content.ReadFromJsonAsync<JsonObject>();

      if (error?["errorCode"]?.ToString() == "205")
      {
        _logger.LogWarning("{url} returned {code} {reason} : {response}",
          request.RequestUri, (int)response.StatusCode, response.ReasonPhrase, await response.Content.ReadAsStringAsync());

        return new MarketDataDto();
      }
      else
      {
        _logger.LogError("{url} returned {code} {reason} : {response}",
          request.RequestUri, (int)response.StatusCode, response.ReasonPhrase, await response.Content.ReadAsStringAsync());

        return null;
      }
    }

    var result = await response.Content.ReadFromJsonAsync<BitvavoMarketDataDto>();

    if (null == result)
    {
      // TODO: Handle.
      throw new Exception("Failed to deserialize response.");
    }

    return _mapper.Map<MarketDataDto>(result);
  }

  public async Task<decimal?> GetPrice(MarketReqDto market)
  {
    using var request = CreateRequestMsg(
      HttpMethod.Get, $"ticker/price?market={market.BaseSymbol.ToUpper()}-{market.QuoteSymbol.ToUpper()}");

    using var response = await _httpClient.SendAsync(request);

    if (!response.IsSuccessStatusCode)
    {
      _logger.LogError("{url} returned {code} {reason} : {response}",
        request.RequestUri, (int)response.StatusCode, response.ReasonPhrase, await response.Content.ReadAsStringAsync());

      return null;
    }

    var result = await response.Content.ReadFromJsonAsync<BitvavoTickerPriceDto>();

    if (null == result)
    {
      // TODO: Handle.
      throw new Exception("Failed to deserialize response.");
    }

    return decimal.Parse(result.Price);
  }

  public Task<OrderDto> NewOrder(OrderReqDto order)
  {
    throw new NotImplementedException();
  }

  public Task<OrderDto?> GetOrder(string orderId, MarketReqDto? market = null)
  {
    throw new NotImplementedException();
  }

  public Task<OrderDto?> CancelOrder(string orderId, MarketReqDto? market = null)
  {
    throw new NotImplementedException();
  }

  public Task<IEnumerable<OrderDto>?> GetOpenOrders(MarketReqDto? market = null)
  {
    throw new NotImplementedException();
  }

  public Task<IEnumerable<OrderDto>?> CancelAllOpenOrders(MarketReqDto? market = null)
  {
    return Task.FromResult((IEnumerable<OrderDto>?)new List<OrderReqDto>());
  }

  public Task<IEnumerable<OrderDto>?> SellAllPositions(string? asset = null)
  {
    throw new NotImplementedException();
  }
}
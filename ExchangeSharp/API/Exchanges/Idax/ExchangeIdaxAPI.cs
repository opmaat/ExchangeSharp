/*
MIT LICENSE

Copyright 2017 Tailormade 2018, SL - http://www.tailormade.eu

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace ExchangeSharp
{
    public sealed partial class ExchangeIdaxAPI : ExchangeAPI
    {
        public override string BaseUrl { get; set; } = "https://openapi.idax.pro/api/v2";
        public override string BaseUrlWebSocket { get; set; } = "wss://openws.idax.pro/ws";

        public ExchangeIdaxAPI()
        {
            RequestContentType = "application/x-www-form-urlencoded";
            RequestContentType = "application/json";
            MarketSymbolSeparator = "_";
        }


        public void Test()
        {
            try
            {
                var or = new ExchangeOrderRequest()
                { 
                     IsBuy=false,
                     MarketSymbol= "VITAE_BTC",
                     Amount=0.5m,
                     Price=0.1m
                };
                var co = OnPlaceOrderAsync(or).Result;
                var o = OnGetOpenOrderDetailsAsync("VITAE_BTC").Result;
                OnCancelOrderAsync(o.First().OrderId).Wait();
            }
            catch (Exception ex)
            {

                throw ex;
            }
        }
        
        protected override async Task<ExchangeTicker> OnGetTickerAsync(string marketSymbol)
        {
            JToken obj = await MakeJsonRequestAsync<JToken>($"/ticker?pair={marketSymbol}");
            JToken token = obj["ticker"][0];
            var ticker = ParseTicker(marketSymbol, token);
            ticker.Volume.Timestamp = CryptoUtility.ParseTimestamp(obj["timestamp"], TimestampType.UnixMilliseconds);
            return ticker;
        }
        protected override async Task<IEnumerable<KeyValuePair<string, ExchangeTicker>>> OnGetTickersAsync()
        {
            throw new NotImplementedException();
        }
        private ExchangeTicker ParseTicker(string symbol, JToken token)
        {
            // {"priceChange":"-0.00192300","priceChangePercent":"-4.735","weightedAvgPrice":"0.03980955","prevClosePrice":"0.04056700","lastPrice":"0.03869000","lastQty":"0.69300000","bidPrice":"0.03858500","bidQty":"38.35000000","askPrice":"0.03869000","askQty":"31.90700000","openPrice":"0.04061300","highPrice":"0.04081900","lowPrice":"0.03842000","volume":"128015.84300000","quoteVolume":"5096.25362239","openTime":1512403353766,"closeTime":1512489753766,"firstId":4793094,"lastId":4921546,"count":128453}
            var ticker = this.ParseTicker(token, symbol, "high", "low", "last", "volume");
            return ticker;
        }

        protected override async Task OnCancelOrderAsync(string orderId, string marketSymbol = null)
        {
            Dictionary<string, object> payload = await GetNoncePayloadAsync();
            //payload["Authorization"] = $"{AuthHeaderPrefix} <{autenticationToken}>";
            //var symbol= order.MarketSymbol;
            payload["orderId"] = orderId;
            JToken token = await MakeJsonRequestAsync<JToken>($"/cancelOrder", null, payload, "POST");

        }
        protected override async Task<ExchangeOrderResult> OnPlaceOrderAsync(ExchangeOrderRequest order)
        {
            Dictionary<string, object> payload = await GetNoncePayloadAsync();
            //payload["Authorization"] = $"{AuthHeaderPrefix} <{autenticationToken}>";
            //var symbol= order.MarketSymbol;

            payload["pair"] = order.MarketSymbol;
            if (order.OrderType == OrderType.Stop)
                throw new Exception("OrderType stop is not available");
            
            payload["orderType"] = order.OrderType.ToStringLowerInvariant();
            payload["orderSide"] = order.IsBuy ? "buy" : "sell";

            // Binance has strict rules on which prices and quantities are allowed. They have to match the rules defined in the market definition.
            decimal outputQuantity = await ClampOrderQuantity(order.MarketSymbol, order.Amount);
            decimal outputPrice = await ClampOrderPrice(order.MarketSymbol, order.Price);
            payload["price"] = outputPrice.ToStringInvariant();

            // Binance does not accept quantities with more than 20 decimal places.
            payload["amount"] = Math.Round(outputQuantity, 20).ToStringInvariant();

            JToken token = await MakeJsonRequestAsync<JToken>($"/placeOrder", null, payload, "POST");
            return ParseOrder(token);
        }

        protected override async Task<IEnumerable<ExchangeOrderResult>> OnGetCompletedOrderDetailsAsync(string marketSymbol = null, DateTime? afterDate = null)
        {
            if (string.IsNullOrEmpty(marketSymbol))
            {
                throw new Exception("marketSymbol cannot be empty");
            }

            Dictionary<string, object> payload = GetNoncePayloadAsync().Result;
            payload["pair"] = marketSymbol;
            payload["orderState"] = 1;
            payload["currentPage"] = 1;
            payload["pageLength"] = 100;
            Dictionary<string, object> payload24 = GetNoncePayloadAsync().Result;
            payload.CopyTo(payload24);
            var obj = await MakeJsonRequestAsync<JToken>($"/orderHistory", null, payload, "POST");
            var array = (JArray)obj["orders"];
            var orders=ParseOrders(marketSymbol, array);

            payload24["orderSide"] = 0;
            afterDate = new DateTime(2019, 8, 29);
            payload24["startTime"] = (Int64)((afterDate == null || afterDate == System.DateTime.MinValue) ? 1 : Math.Round(afterDate.Value.UnixTimestampFromDateTimeMilliseconds()));
            //payload["startTime"] = (afterDate == null || afterDate == System.DateTime.MinValue) ? 1: Math.Round(afterDate.Value.UnixTimestampFromDateTimeMilliseconds());
            payload24["endTime"] = (Int64)Math.Round(System.DateTime.UtcNow.AddHours(-24).UnixTimestampFromDateTimeMilliseconds());

            var obj24 = await MakeJsonRequestAsync<JToken>($"/beforeOrderHistory", null, payload24, "POST");
            var array24 = (JArray)obj24["orders"];
            var orders24=ParseOrders(marketSymbol, array24);
            orders.AddRange(orders24.Where(_=>!orders.Any(__=>__.OrderId==_.OrderId)));
            return orders;
        }

        private List<ExchangeOrderResult> ParseOrders(string marketSymbol,JArray array)
        {
            List<ExchangeOrderResult> orders = new List<ExchangeOrderResult>();
            foreach (JToken token in array)
            {
                var order = ParseOrder(token);
                order.MarketSymbol = marketSymbol;
                orders.Add(order);
            }
            return orders;
        }

        protected override async Task<IEnumerable<ExchangeOrderResult>> OnGetOpenOrderDetailsAsync(string marketSymbol = null)
        {
            if (string.IsNullOrEmpty(marketSymbol))
            {
                throw new Exception("marketSymbol cannot be empty");
            }
            List<ExchangeOrderResult> orders = new List<ExchangeOrderResult>();
            Dictionary<string, object> payload = GetNoncePayloadAsync().Result;
            payload["pair"] = marketSymbol;
            payload["orderId"] = -1;
            payload["pageIndex"] = 0;
            payload["pageSize"] = 300;
            var obj = await MakeJsonRequestAsync<JToken>($"/orderInfo", null, payload,"POST");
            var array = (JArray)obj["orders"];
            foreach (JToken token in array)
            {
                var order = ParseOrder(token);
                order.MarketSymbol = marketSymbol;
                orders.Add(order);
            }
            return orders;
        }

        private ExchangeOrderResult ParseOrder(JToken token)
        {
            /*
              {
    "quantity": "160.00",
    "timestamp": 1569305973395,
    "avgPrice": "0",
    "dealQuantity": "0",
    "orderId": 3261550990000011862,
    "price": "0.00020250",
    "orderState": 1,
    "orderSide": "sell",
    "triggerPrice": null,
    "priceGap": null,
    "orderMode": 0,
    "orderProperty": 0
}

            */
            ExchangeOrderResult result = new ExchangeOrderResult
            {
                Amount = token["quantity"].ConvertInvariant<decimal>(),
                AmountFilled = token["dealQuantity"].ConvertInvariant<decimal>(),
                Price = token["price"].ConvertInvariant<decimal>(),
                AveragePrice = token["avgPrice"].ConvertInvariant<decimal>(),
                IsBuy = token["orderSide"].ToStringInvariant().ToUpper() == "BUY",
                OrderDate = CryptoUtility.ParseTimestamp(token["timestamp"], TimestampType.UnixMilliseconds),
                OrderId = token["orderId"].ToStringInvariant(),
                //MarketSymbol = token["symbol"].ToStringInvariant()
            };

            switch (token["orderState"].ToStringUpperInvariant())
            {
                case "NEW":
                case "1":
                    result.Result = ExchangeAPIOrderResult.Pending;
                    break;

                case "PARTIALLY_FILLED":
                case "2":
                    result.Result = ExchangeAPIOrderResult.FilledPartially;
                    break;

                case "FILLED":
                case "9":
                    result.Result = ExchangeAPIOrderResult.Filled;
                    break;

                case "CANCELED":
                case "CANCELLED":
                case "PENDING_CANCEL":
                case "EXPIRED":
                case "REJECTED":
                case "19":
                    result.Result = ExchangeAPIOrderResult.Canceled;
                    break;

                default:
                    result.Result = ExchangeAPIOrderResult.Error;
                    break;
            }

            //ParseAveragePriceAndFeesFromFills(result, token["fills"]);

            return result;
        }

        protected override async Task<IEnumerable<string>> OnGetMarketSymbolsAsync()
        {
            List<string> symbols = new List<string>();
            JToken obj = await MakeJsonRequestAsync<JToken>("/pairs");
            JArray array = (JArray)obj["pairs"];
            foreach (string symbol in array)
            {
                symbols.Add(symbol);
            }
            return symbols;
        }


        protected override async Task<Dictionary<string, decimal>> OnGetAmountsAsync()
        {
            Dictionary<string, decimal> amounts = new Dictionary<string, decimal>();
            Dictionary<string, object> payload = GetNoncePayloadAsync().Result;

            var result = await MakeJsonRequestAsync<JToken>($"/userinfo", null, payload, "POST");
            foreach (JProperty token in result["total"])
            {
                decimal amount = token.Value.ConvertInvariant<decimal>();
                if (amount > 0m) amounts[token.Name] = amount;
            }
            return amounts;
        }

        protected override async Task<Dictionary<string, decimal>> OnGetAmountsAvailableToTradeAsync()
        {
            Dictionary<string, decimal> amounts = new Dictionary<string, decimal>();
            Dictionary<string, object> payload = GetNoncePayloadAsync().Result;
            
            var result = await MakeJsonRequestAsync<JToken>($"/userinfo", null, payload,"POST");
            foreach (JProperty token in result["free"])
            {
                decimal amount = token.Value.ConvertInvariant<decimal>();
                if (amount > 0m) amounts[token.Name] = amount;
            }
            return amounts;
        }


        protected override async Task<IEnumerable<ExchangeMarket>> OnGetMarketSymbolsMetadataAsync()
        {
            List<ExchangeMarket> markets = new List<ExchangeMarket>();
            JToken obj = await MakeJsonRequestAsync<JToken>("/pairList");
            JToken array = obj["pairList"];
            foreach (JToken token in array)
            {
                markets.Add(new ExchangeMarket
                {
                    MarketId = token["pairName"].ToStringInvariant(),
                    MarketSymbol = token["pairName"].ToStringInvariant(),
                    BaseCurrency = token["baseCoinCode"].ToStringInvariant(),
                    QuoteCurrency = token["quoteCoinCode"].ToStringInvariant(),
                    MinTradeSize = token["minAmount"].ConvertInvariant<decimal>(),
                    MaxTradeSize = token["maxAmount"].ConvertInvariant<decimal>(),

                    //QuantityStepSize = token["lotSize"].ConvertInvariant<decimal>(),

                    MinPrice = 1m / (decimal)Math.Pow(10, token["priceDecimalPlace"].ConvertInvariant<int>()),
                    MaxPrice = 90000000m,
                    //PriceStepSize = 1m / (decimal)Math.Pow(10 , token["pricePrecision"].ConvertInvariant<int>()),

                    MinTradeSizeInQuoteCurrency = 1m / (decimal)Math.Pow(10, token["qtyDecimalPlace"].ConvertInvariant<int>()),
                    //MaxTradeSizeInQuoteCurrency

                    IsActive = (token["status"].ToStringLowerInvariant() == "open"),
                    MarginEnabled = false,
                });
                /*
                 {
		            "pairName": "ETH_BTC",
		            "status": "Open",
		            "maxAmount": "1000000000000.00000000",
		            "minAmount": "0.00100000",
		            "priceDecimalPlace": 6,
		            "qtyDecimalPlace": 3,
		            "baseCoinCode": "ETH",
		            "quoteCoinCode": "BTC"
	            }
                 */
            }
            return markets;
        }
        protected override async Task<IReadOnlyDictionary<string, ExchangeCurrency>> OnGetCurrenciesAsync()
        {
            var currencies = new Dictionary<string, ExchangeCurrency>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, object> payload = await GetNoncePayloadAsync();

            JToken result = await MakeJsonRequestAsync<JToken>("/coinList", payload: payload);
            JToken array = result["coinList"];
            foreach (JToken token in array)
            {
                /*
                 {
		            "coinCode": "BTC",
		            "coinName": "Bitcoin",
		            "canDeposit": true,
		            "canWithdraw": true,
		            "minWithdrawal": 0.00100000
                    }
                 */
                var coin = new ExchangeCurrency
                {
                    //BaseAddress = token["BaseAddress"].ToStringInvariant(),
                    //CoinType = token["CoinType"].ToStringInvariant(),
                    FullName = token["coinName"].ToStringInvariant(),
                    DepositEnabled = token["canDeposit"].ConvertInvariant<bool>(),
                    WithdrawalEnabled = token["canWithdraw"].ConvertInvariant<bool>(),
                    //MinConfirmations = token["MinConfirmation"].ConvertInvariant<int>(),
                    Name = token["coinCode"].ToStringUpperInvariant(),
                    //Notes = token["Notice"].ToStringInvariant(),
                    //TxFee = token["TxFee"].ConvertInvariant<decimal>(),
                    AmountPrecision = token["amountPrecision"].ConvertInvariant<int>(),
                    MinWithdrawalSize = token["minWithdrawal"].ConvertInvariant<decimal>(),
                };

                currencies[coin.Name] = coin;
                // Available fields not persisted in the model
                /*
                {
                    "id": "5c4b0deb929d4f9d15a16cc6",
                  "isCrypto": true,
                  "safeName": "bitcoin",
                  "autoWithdrawalEnabled": false,
                  "hotWalletMaxAmount": 0,
                  "hotWalletTransferMinThreshold": 0,                  
                }*/
            }
            return currencies;
        }
        protected override Task ProcessRequestAsync(IHttpWebRequest request, Dictionary<string, object> payload)
        {
            if (CanMakeAuthenticatedRequest(payload))
            {
                payload.Remove("nonce");
                //var bodyPayload = payload.Where(_ => _.Key.StartsWith("body.")).ToDictionary(_ => _.Key.Substring(5),_=>_.Value);
                //var headerPayload = payload.Where(_ => !_.Key.StartsWith("body.")).ToDictionary(_ => _.Key, _ => _.Value);
                //foreach (var item in headerPayload)
                //{
                //    request.AddHeader(item.Key, item.Value.ToString());
                //}

                //if (request.Method == "POST" && bodyPayload.Count()>0)
                if (request.Method == "POST")
                {
                    //var query = CryptoUtility.GetFormForPayload(bodyPayload);
                    payload["key"] = PublicApiKey.ToUnsecureString();
                    payload["timestamp"] = (Int64)DateTime.UtcNow.UnixTimestampFromDateTimeMilliseconds();
                    var msg = string.Join("&", 
                            payload.Where(_ => !string.IsNullOrEmpty(payload[_.Key].ToString()))
                            .OrderBy(_ => _.Key)
                            .Select(_=> string.Format("{0}={1}",_.Key.UrlEncode(), _.Value.ToStringInvariant().UrlEncode()))
                        );
                    payload["sign"] = CryptoUtility.SHA256Sign(msg, PrivateApiKey.ToUnsecureString());

                    var form = request.WritePayloadJsonToRequestAsync(payload).Result;
                }

            }
            return base.ProcessRequestAsync(request, payload);
        }
        /*
        protected override Uri ProcessRequestUrl(UriBuilder url, Dictionary<string, object> payload, string method)
        {
            if (CanMakeAuthenticatedRequest(payload))
            {
                var bodyPayload = payload.Where(_ => _.Key.StartsWith("body.")).ToDictionary(_ => _.Key.Substring(5), _ => _.Value);

                // payload is ignored, except for the nonce which is added to the url query - bittrex puts all the "post" parameters in the url query instead of the request body
                var query = (url.Query ?? string.Empty).Trim('?', '&');
                string newQuery = (query.Length != 0 ? "&" + query : string.Empty) +
                    (bodyPayload.Count > 0 ? "&" + CryptoUtility.GetFormForPayload(bodyPayload, false) : string.Empty);
                url.Query = newQuery;
                return url.Uri;
            }
            return base.ProcessRequestUrl(url, payload, method);
        }
        */
    }
}
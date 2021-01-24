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
    public sealed partial class ExchangeBitBlinxAPI : ExchangeAPI
    {
        public override string BaseUrl { get; set; } = "https://trade.bitblinx.com/api";
        public override string BaseUrlWebSocket { get; set; } = "wss://trade.bitblinx.com/ws";
        public string AuthHeaderPrefix { get; set; } = "sx";
        public string ConsumerToken { get; set; }
        public string Auhtorization { get; set; }
        private string autenticationToken;
        private string userId;

        public ExchangeBitBlinxAPI()
        {
            RequestContentType = "application/x-www-form-urlencoded";            
        }

        public void Authenticate(string consumerToken, string authorization)
        {
            this.ConsumerToken = consumerToken;
            this.Auhtorization = authorization;
            Dictionary<string, object> payload = GetNoncePayloadAsync().Result;
            payload["Consumer"] = $"{AuthHeaderPrefix} <{ConsumerToken}>";
            payload["Authorization"] = $"{AuthHeaderPrefix} <{Auhtorization}>";
            JToken result = MakeJsonRequestAsync<JToken>("/auth/login", payload: payload, requestMethod: "POST").Result;
            autenticationToken = result["token"].ToStringInvariant();
            userId = result["userId"].ToStringInvariant();
        }

        public void Test()
        {
            try
            {
                var o = OnGetOpenOrderDetailsAsync().Result;
                OnCancelOrderAsync(o.First().OrderId).Wait();
            }
            catch (Exception ex)
            {

                throw ex;
            }

        }

        protected override bool CanMakeAuthenticatedRequest(IReadOnlyDictionary<string, object> payload)
        {
            return (ConsumerToken != null && (autenticationToken != null || payload.ContainsKey("Authorization")) && payload != null && payload.ContainsKey("nonce"));
        }

        protected override async Task<ExchangeTicker> OnGetTickerAsync(string marketSymbol)
        {
            JToken obj = await MakeJsonRequestAsync<JToken>($"/prices/{NormalizeMarketSymbol(marketSymbol)}");
            var token = obj.FirstOrDefault();
            var ticker = ParseTicker(NormalizeMarketSymbol( token["symbol"].ToStringInvariant()), token);
            ticker.MarketSymbol = token["symbol"].ToStringInvariant();
            return ticker;
        }
        protected override async Task<IEnumerable<KeyValuePair<string, ExchangeTicker>>> OnGetTickersAsync()
        {
            List<KeyValuePair<string, ExchangeTicker>> tickers = new List<KeyValuePair<string, ExchangeTicker>>();
            JToken obj = await MakeJsonRequestAsync<JToken>("/prices");
            foreach (JToken token in obj)
            {
                var ticker = ParseTicker(NormalizeMarketSymbol(token["symbol"].ToStringInvariant()), token);
                ticker.MarketSymbol = token["symbol"].ToStringInvariant();
                tickers.Add(new KeyValuePair<string, ExchangeTicker>(ticker.MarketSymbol, ticker));
            }
            return tickers;
        }
        private ExchangeTicker ParseTicker(string symbol, JToken token)
        {
            // {"priceChange":"-0.00192300","priceChangePercent":"-4.735","weightedAvgPrice":"0.03980955","prevClosePrice":"0.04056700","lastPrice":"0.03869000","lastQty":"0.69300000","bidPrice":"0.03858500","bidQty":"38.35000000","askPrice":"0.03869000","askQty":"31.90700000","openPrice":"0.04061300","highPrice":"0.04081900","lowPrice":"0.03842000","volume":"128015.84300000","quoteVolume":"5096.25362239","openTime":1512403353766,"closeTime":1512489753766,"firstId":4793094,"lastId":4921546,"count":128453}
            var ticker = this.ParseTicker(token, symbol, "ask", "bid", "last", "volume.base", "volume.quote", "timestamp", TimestampType.Iso8601);
            JToken volume = token["volume"];
            ticker.Volume.BaseCurrencyVolume = volume["base"].ConvertInvariant<decimal>();
            ticker.Volume.QuoteCurrencyVolume = volume["quote"].ConvertInvariant<decimal>();
            return ticker;
        }

        protected override async Task OnCancelOrderAsync(string orderId, string marketSymbol = null)
        {
            Dictionary<string, object> payload = await GetNoncePayloadAsync();
            payload["Authorization"] = $"{AuthHeaderPrefix} <{autenticationToken}>";
            //var symbol= order.MarketSymbol;

            JToken token = await MakeJsonRequestAsync<JToken>($"/orders/{orderId}", null, payload, "DELETE");
            
        }
        protected override async Task<ExchangeOrderResult> OnPlaceOrderAsync(ExchangeOrderRequest order)
        {
            Dictionary<string, object> payload = await GetNoncePayloadAsync();
            payload["Authorization"] = $"{AuthHeaderPrefix} <{autenticationToken}>";
            //var symbol= order.MarketSymbol;

            payload["body.symbol"] = order.MarketSymbol.Replace('-','/');
            payload["body.side"] = order.IsBuy ? "buy" : "sell";
            if (order.OrderType == OrderType.Stop)
                payload["body.type"] = "stop_loss"; //if order type is stop loss/limit, then binance expect word 'STOP_LOSS' inestead of 'STOP'
            else
                payload["body.type"] = order.OrderType.ToStringLowerInvariant();

            // Binance has strict rules on which prices and quantities are allowed. They have to match the rules defined in the market definition.
            decimal outputQuantity = await ClampOrderQuantity(order.MarketSymbol, order.Amount);
            decimal outputPrice = await ClampOrderPrice(order.MarketSymbol, order.Price);

            // Binance does not accept quantities with more than 20 decimal places.
            payload["body.quantity"] = Math.Round(outputQuantity, 20);
            payload["body.price"] = outputPrice;
            
            JToken token = await MakeJsonRequestAsync<JToken>($"/orders", null, payload, "POST");
            return ParseOrder(token);
        }

        protected override async Task<IEnumerable<ExchangeOrderResult>> OnGetCompletedOrderDetailsAsync(string marketSymbol = null, DateTime? afterDate = null)
        {
            List<ExchangeOrderResult> orders = new List<ExchangeOrderResult>();
            Dictionary<string, object> payload = GetNoncePayloadAsync().Result;
            payload["Authorization"] = $"{AuthHeaderPrefix} <{autenticationToken}>";
            var startDateTimestamp = (afterDate == null || afterDate==System.DateTime.MinValue) ? 1 : Math.Round( afterDate.Value.UnixTimestampFromDateTimeMilliseconds());
            var endDateTimestamp = Math.Round( System.DateTime.UtcNow.UnixTimestampFromDateTimeMilliseconds());
            //payload["body.endDate"] = endDateTimestamp;
            var page = 1;
            var itemsPerPage = 500;
            var hideCanceled = false.ToStringLowerInvariant();
            var pairDashed = string.IsNullOrEmpty( marketSymbol)? "any":marketSymbol;
            var side = "ANY";
            var orderType = "ANY";
            var obj = await MakeJsonRequestAsync<JToken>($"/orders/history?startDate={startDateTimestamp}&endDate={endDateTimestamp}&page={page}&itemsPerPage={itemsPerPage}&hideCanceled={hideCanceled}&symbol={pairDashed}&side={side}&type={orderType}", null, payload,"GET");
            foreach (JToken token in obj)
            {
                orders.Add(ParseOrder(token));
            }
            return orders;
        }
        protected override async Task<IEnumerable<ExchangeOrderResult>> OnGetOpenOrderDetailsAsync(string marketSymbol = null)
        {
            List<ExchangeOrderResult> orders = new List<ExchangeOrderResult>();
            Dictionary<string, object> payload = GetNoncePayloadAsync().Result;
            payload["Authorization"] = $"{AuthHeaderPrefix} <{autenticationToken}>";

            var obj = await MakeJsonRequestAsync<JToken>($"/open-orders", null, payload);
            foreach (JToken token in obj)
            {
                orders.Add(ParseOrder(token));
            }
            return orders;
        }

        private ExchangeOrderResult ParseOrder(JToken token)
        {
            /*
              [
        {
            "orderID": "GTFTA-BTC-37058-1566969620678",
            "userId": "5d49a9fb946e8c6a29dd3e0f",
            "side": "sell",
            "type": "limit",
            "subType": "gtc",
            "price": "0.0002000000",
            "averagePrice": "0.0000000000",
            "quantity": "1.000000",
            "cumQuantity": "0.000000",
            "symbol": "GTFTA/BTC",
            "status": "new",
            "created": "2019-08-28T05:20:20.678Z",
            "updated": "2019-08-28T05:20:20.678Z",
            "email": "raouljacobs@hotmail.com"
        },
        {
            "orderID": "GTFTA-BTC-30015-1566918271314",
            "userId": "5d49a9fb946e8c6a29dd3e0f",
            "side": "sell",
            "type": "limit",
            "subType": "gtc",
            "price": "0.0002000000",
            "averagePrice": "0.0000000000",
            "quantity": "1.000000",
            "cumQuantity": "0.000000",
            "symbol": "GTFTA/BTC",
            "status": "new",
            "created": "2019-08-27T15:04:31.315Z",
            "updated": "2019-08-27T15:04:31.315Z",
            "email": "raouljacobs@hotmail.com"
        }
    ]
            */
            ExchangeOrderResult result = new ExchangeOrderResult
            {
                Amount = token["quantity"].ConvertInvariant<decimal>(),
                AmountFilled = token["cumQuantity"].ConvertInvariant<decimal>(),
                Price = token["price"].ConvertInvariant<decimal>(),
                AveragePrice = token["averagePrice"].ConvertInvariant<decimal>(),
                IsBuy = token["side"].ToStringInvariant().ToUpper() == "BUY",
                OrderDate = token["created"].ToDateTimeInvariant(),
                OrderId = token["orderID"].ToStringInvariant(),
                MarketSymbol =  token["symbol"].ToStringInvariant()
            };

            switch (token["status"].ToStringUpperInvariant())
            {
                case "NEW":
                    result.Result = ExchangeAPIOrderResult.Pending;
                    break;

                case "PARTIALLY_FILLED":
                    result.Result = ExchangeAPIOrderResult.FilledPartially;
                    break;

                case "FILLED":
                    result.Result = ExchangeAPIOrderResult.Filled;
                    break;

                case "CANCELED":
                case "CANCELLED":
                case "PENDING_CANCEL":
                case "EXPIRED":
                case "REJECTED":
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
            JToken obj = await MakeJsonRequestAsync<JToken>("/symbols");
            foreach (JToken token in obj)
            {
                symbols.Add(token["symbol"].ToStringInvariant());
            }
            return symbols;
        }


        protected override async Task<Dictionary<string, decimal>> OnGetAmountsAsync()
        {
            Dictionary<string, decimal> amounts = new Dictionary<string, decimal>();
            Dictionary<string, object> payload = GetNoncePayloadAsync().Result;
            payload["Authorization"] = $"{AuthHeaderPrefix} <{autenticationToken}>";

            var result = await MakeJsonRequestAsync<JToken>($"/user/{userId}/balances", null, payload);
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
            payload["Authorization"] = $"{AuthHeaderPrefix} <{autenticationToken}>";

            var result = await MakeJsonRequestAsync<JToken>($"/user/{userId}/balances", null, payload);
            foreach (JProperty token in result["available"])
            {
                decimal amount = token.Value.ConvertInvariant<decimal>();
                if (amount > 0m) amounts[token.Name] = amount;
            }
            return amounts;
        }


        protected override async Task<IEnumerable<ExchangeMarket>> OnGetMarketSymbolsMetadataAsync()
        {
            List<ExchangeMarket> markets = new List<ExchangeMarket>();
            JToken obj = await MakeJsonRequestAsync<JToken>("/symbols");
            foreach (JToken token in obj)
            {
                markets.Add(new ExchangeMarket
                {
                    MarketId = token["id"].ToStringInvariant(),
                    MarketSymbol = token["symbol"].ToStringInvariant(),
                    BaseCurrency = token["base"].ToStringInvariant(),
                    QuoteCurrency = token["quote"].ToStringInvariant(),
                    MinTradeSize = token["minOrderAmount"].ConvertInvariant<decimal>(),
                    MaxTradeSize = 90000000m,
                    QuantityStepSize = token["lotSize"].ConvertInvariant<decimal>(),

                    MinPrice = 1m / (decimal)Math.Pow(10 , token["pricePrecision"].ConvertInvariant<int>()),
                    MaxPrice = 90000000m,
                    PriceStepSize = 1m / (decimal)Math.Pow(10 , token["pricePrecision"].ConvertInvariant<int>()),

                    MinTradeSizeInQuoteCurrency = 1m / (decimal)Math.Pow(10 ,token["amountPrecision"].ConvertInvariant<int>()),
                    //MaxTradeSizeInQuoteCurrency

                    IsActive = token["isActive"].ConvertInvariant<bool>(),
                    MarginEnabled = token["isMarginActive"].ConvertInvariant<bool>(),                    
                });
                /*
                 {
                  "totalPrecision": 2,
                  "price": "0.0000000000",
                  "ask": "0.0000000000",
                  "bid": "0.0000000000",
                  "volume": "0.0000",
                  "baseSafeName": "bitcoin",
                  "quoteSafeName": "dollar-online"
                }
                 */
            }
            return markets;
        }
        protected override async Task<IReadOnlyDictionary<string, ExchangeCurrency>> OnGetCurrenciesAsync()
        {
            var currencies = new Dictionary<string, ExchangeCurrency>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, object> payload = await GetNoncePayloadAsync();
            payload["Consumer"] = $"{AuthHeaderPrefix} <{ConsumerToken}>";
            payload["Authorization"] = $"{AuthHeaderPrefix} <{autenticationToken}>";

            JToken array = await MakeJsonRequestAsync<JToken>("/currencies", payload: payload);
            foreach (JToken token in array)
            {
                bool enabled = token["isActive"].ConvertInvariant<bool>();
                var coin = new ExchangeCurrency
                {
                    //BaseAddress = token["BaseAddress"].ToStringInvariant(),
                    //CoinType = token["CoinType"].ToStringInvariant(),
                    FullName = token["name"].ToStringInvariant(),
                    DepositEnabled = token["depositActive"].ConvertInvariant<bool>(),
                    WithdrawalEnabled = token["withdrawActive"].ConvertInvariant<bool>(),
                    //MinConfirmations = token["MinConfirmation"].ConvertInvariant<int>(),
                    Name = token["symbol"].ToStringUpperInvariant(),
                    //Notes = token["Notice"].ToStringInvariant(),
                    //TxFee = token["TxFee"].ConvertInvariant<decimal>(),
                    AmountPrecision = token["amountPrecision"].ConvertInvariant<int>(),
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
                var bodyPayload = payload.Where(_ => _.Key.StartsWith("body.")).ToDictionary(_ => _.Key.Substring(5),_=>_.Value);
                var headerPayload = payload.Where(_ => !_.Key.StartsWith("body.")).ToDictionary(_ => _.Key, _ => _.Value);
                foreach (var item in headerPayload)
                {
                    request.AddHeader(item.Key, item.Value.ToString());
                }
                
                if (request.Method == "POST" && bodyPayload.Count()>0)
                {
                    //var query = CryptoUtility.GetFormForPayload(bodyPayload);
                    var form= request.WritePayloadFormToRequestAsync(bodyPayload).Result;
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
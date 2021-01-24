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

        public void Authenticate(string consumerToken, string login, string password)
        {
            this.ConsumerToken = consumerToken;
            this.Auhtorization = $"{login}:{password}";
            Dictionary<string, object> payload = GetNoncePayloadAsync().Result;
            payload["Consumer"] = $"{AuthHeaderPrefix} <{ConsumerToken}>";
            payload["Authorization"] = $"{AuthHeaderPrefix} <{Auhtorization}>";
            JToken result = MakeJsonRequestAsync<JToken>("/auth/login", payload: payload, requestMethod: "POST").Result;
            autenticationToken = result["token"].ToStringInvariant();
            userId = result["userId"].ToStringInvariant();
        }

        protected override bool CanMakeAuthenticatedRequest(IReadOnlyDictionary<string, object> payload)
        {
            return (ConsumerToken != null && (autenticationToken != null || payload.ContainsKey("Authorization")) && payload != null && payload.ContainsKey("nonce"));
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

        public void Test()
        {
            try
            {
                var x = OnGetAmountsAvailableToTradeAsync().Result;
            }
            catch (Exception ex)
            {

                throw ex;
            }
            
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

                    MinPrice = 1m / (10 ^ token["pricePrecision"].ConvertInvariant<int>()),
                    MaxPrice = 90000000m,
                    PriceStepSize = 1m / (10 ^ token["pricePrecision"].ConvertInvariant<int>()),

                    //MinTradeSizeInQuoteCurrency
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
        protected override async Task ProcessRequestAsync(IHttpWebRequest request, Dictionary<string, object> payload)
        {
            if (CanMakeAuthenticatedRequest(payload))
            {
                payload.Remove("nonce");
                foreach (var item in payload)
                {
                    request.AddHeader(item.Key, item.Value.ToString());
                }
                /*
                string body = CryptoUtility.GetJsonForPayload(payload);
                
                if (request.Method == "POST")
                {
                    await CryptoUtility.WriteToRequestAsync(request, body);
                }
                */
            }

        }
    }
}
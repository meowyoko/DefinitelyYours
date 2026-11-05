using System.Collections.Generic;
using Newtonsoft.Json;

namespace AiPhone
{
    internal sealed class CatalogRequest
    {
        [JsonProperty("day")]
        public int Day { get; set; }

        [JsonProperty("items")]
        public List<CatalogItem> Items { get; set; } = new List<CatalogItem>();
    }

    internal sealed class CatalogItem
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("types")]
        public string[] Types { get; set; }

        [JsonProperty("baseValue")]
        public int BaseValue { get; set; }

        [JsonProperty("contraband")]
        public bool Contraband { get; set; }

        [JsonProperty("stolen")]
        public bool Stolen { get; set; }
    }

    internal sealed class GiftHints
    {
        [JsonProperty("profession")]
        public string Profession { get; set; }

        [JsonProperty("lowStock")]
        public List<GiftHintItem> LowStock { get; set; } = new List<GiftHintItem>();

        [JsonProperty("soldToday")]
        public List<GiftHintItem> SoldToday { get; set; } = new List<GiftHintItem>();

        [JsonProperty("suggested")]
        public List<string> Suggested { get; set; } = new List<string>();
    }

    internal sealed class GiftHintItem
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("count")]
        public int Count { get; set; }
    }

    internal sealed class ParcelPickupRequest
    {
        [JsonProperty("day")]
        public int Day { get; set; }
    }

    internal sealed class ParcelPickupResponse
    {
        [JsonProperty("parcel")]
        public ParcelOrder Parcel { get; set; }
    }

    internal sealed class ParcelOrder
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("style")]
        public string Style { get; set; }

        [JsonProperty("items")]
        public List<ParcelOrderItem> Items { get; set; } = new List<ParcelOrderItem>();

        [JsonProperty("note")]
        public ParcelNote Note { get; set; }
    }

    internal sealed class ParcelOrderItem
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("amount")]
        public int Amount { get; set; }
    }

    internal sealed class ParcelNote
    {
        [JsonProperty("title")]
        public string Title { get; set; }

        [JsonProperty("body")]
        public string Body { get; set; }
    }

    internal sealed class ParcelResultRequest
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("placed")]
        public string Placed { get; set; }

        [JsonProperty("rejected")]
        public List<string> Rejected { get; set; } = new List<string>();
    }

    internal sealed class SessionRequest
    {
        [JsonProperty("day")]
        public int Day { get; set; }
    }

    internal sealed class PhoneConfig
    {
        [JsonProperty("baseUrl")]
        public string BaseUrl { get; set; }

        [JsonProperty("token")]
        public string Token { get; set; }
    }
}

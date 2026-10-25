using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.JSInterop;

namespace ZsebApp.Services;

/// <summary>Vékony réteg a böngésző localStorage-e fölött.</summary>
public sealed class LocalStore(IJSRuntime js)
{
    public async Task<T?> GetAsync<T>(string key, JsonTypeInfo<T> typeInfo)
    {
        var json = await js.InvokeAsync<string?>("localStorage.getItem", key);
        if (string.IsNullOrEmpty(json))
            return default;

        try
        {
            return JsonSerializer.Deserialize(json, typeInfo);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    public ValueTask SetAsync<T>(string key, T value, JsonTypeInfo<T> typeInfo) =>
        js.InvokeVoidAsync("localStorage.setItem", key, JsonSerializer.Serialize(value, typeInfo));

    public async Task<string?> GetRawAsync(string key) =>
        await js.InvokeAsync<string?>("localStorage.getItem", key);

    public Task SetRawAsync(string key, string json) =>
        js.InvokeVoidAsync("localStorage.setItem", key, json).AsTask();

    public ValueTask RemoveAsync(string key) =>
        js.InvokeVoidAsync("localStorage.removeItem", key);
}

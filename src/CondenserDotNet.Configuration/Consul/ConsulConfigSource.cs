﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CondenserDotNet.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;

namespace CondenserDotNet.Configuration.Consul
{
    public class ConsulConfigSource : IConfigSource
    {
        private const string ConsulPath = "/";
        private const char CorePath = ':';
        private const string ConsulKeyPath = "/v1/kv/";

        private readonly HttpClient _httpClient;
        private readonly CancellationTokenSource _disposed = new CancellationTokenSource();
        private readonly IKeyParser _parser;
        private ILogger _logger;

        public ConsulConfigSource(IOptions<ConsulRegistryConfig> agentConfig, ILogger logger)
        {
            _logger = logger;
            var agentInfo = agentConfig?.Value ?? new ConsulRegistryConfig();
            _parser = agentInfo.KeyParser;

            _httpClient = HttpUtils.CreateClient(agentInfo.AgentAddress, agentInfo.AgentPort);
        }

        private class WatchConsul
        {
            public string ConsulIndex { get; set; }
        }

        public object CreateWatchState() => new WatchConsul
        {
            ConsulIndex = "0"
        };

        public string FormValidKey(string keyPath)
        {
            if (!keyPath.EndsWith(ConsulPath))
            {
                keyPath = keyPath + ConsulPath;
            }
            if (keyPath.StartsWith(ConsulPath))
            {
                keyPath = keyPath.TrimStart(ConsulPath[0]);
            }
            return keyPath;
        }

        public async Task<(bool success, Dictionary<string, string> dictionary)> GetKeysAsync(string keyPath)
        {
            _logger?.LogTrace("Getting kv from path {ConsulKeyPath}{keyPath}", ConsulKeyPath, keyPath);
            try
            {
                var response = await _httpClient.GetAsync($"{ConsulKeyPath}{keyPath}?recurse");
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("We didn't get a succesful response from consul code was {code}", response.StatusCode);
                    return (false, null);
                }

                var dictionary = await BuildDictionaryAsync(keyPath, response);
                return (true, dictionary);
            }
            catch(Exception ex)
            {
                _logger.LogError(100, ex, "There was an exception getting the keys");
                throw;
            }
        }

        public async Task<(bool success, Dictionary<string, string> update)> TryWatchKeysAsync(string keyPath, object state)
        {
            var consulState = (WatchConsul)state;
            var url = $"{ConsulKeyPath}{keyPath}?recurse&wait=300s&index=";
            var response = await _httpClient.GetAsync(url + consulState.ConsulIndex, _disposed.Token);

            if (!response.IsSuccessStatusCode)
            {
                return (false, null);
            }

            var newConsulIndex = response.GetConsulIndex();

            if (newConsulIndex == consulState.ConsulIndex)
            {
                return (false, null);
            }

            consulState.ConsulIndex = newConsulIndex;
            var dictionary = await BuildDictionaryAsync(keyPath, response);
            return (true, dictionary);
        }

        private async Task<Dictionary<string, string>> BuildDictionaryAsync(string keyPath,
            HttpResponseMessage response)
        {
            var content = await response.Content.ReadAsStringAsync();
            var keys = JsonConvert.DeserializeObject<KeyValue[]>(content);

            var parsedKeys = keys.SelectMany(k => _parser.Parse(k));

            var dictionary = parsedKeys.ToDictionary(
                kv => kv.Key.Substring(keyPath.Length).Replace(ConsulPath[0], CorePath),
                kv => kv.IsDerivedKey ? kv.Value : kv.Value == null ? null : kv.ValueFromBase64(),
                StringComparer.OrdinalIgnoreCase);
            return dictionary;
        }

        public async Task<bool> TrySetKeyAsync(string keyPath, string value)
        {
            var response = await _httpClient.PutAsync($"{ConsulKeyPath}{keyPath}", HttpUtils.GetStringContent(value));
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }
            return true;
        }
    }
}

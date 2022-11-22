﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Specialized;
using Microsoft.Azure.Functions.Worker.Core;
using Microsoft.Azure.Functions.Worker.Converters;
using Newtonsoft.Json;
using System.Linq;

namespace Microsoft.Azure.Functions.Worker
{
    /// <summary>
    /// Converter to bind Blob Storage type parameters.
    /// </summary>
    internal class BlobStorageConverter : IInputConverter
    {
        public ValueTask<ConversionResult> ConvertAsync(ConverterContext context)
        {
            if (context.Source is not CollectionModelBindingData collectionBindingData)
            {
                return new ValueTask<ConversionResult>(ConversionResult.Unhandled());
            }

            if(collectionBindingData != null)
            {
                var collectionResult = ConvertCollectionModelBindingDataAsync(context.TargetType, collectionBindingData);

                if (collectionResult is not null && collectionResult.Any())
                {
                    return new ValueTask<ConversionResult>(ConversionResult.Success(collectionResult));
                }                
            }
            else
            {
                if (context.Source is not ModelBindingData bindingData)
                {
                    return new ValueTask<ConversionResult>(ConversionResult.Unhandled());
                }

                var result = ConvertModelBindingDataAsync(bindingData, context.TargetType);

                if (result is not null)
                {
                    return new ValueTask<ConversionResult>(ConversionResult.Success(result));
                }
            }
            
            return new ValueTask<ConversionResult>(ConversionResult.Unhandled());
        }

        private object? ConvertModelBindingDataAsync(ModelBindingData bindingData, Type TargetType)
        {
            if (bindingData.Source is not Constants.BlobExtensionName)
            {
                return new ValueTask<ConversionResult>(ConversionResult.Unhandled());
            }

            if (!TryGetBindingDataContent(bindingData, out IDictionary<string, string> content))
            {
                return new ValueTask<ConversionResult>(ConversionResult.Unhandled());
            }

            content.TryGetValue(Constants.Connection, out var connectionName);
            content.TryGetValue(Constants.ContainerName, out var containerName);
            content.TryGetValue(Constants.BlobName, out var blobName);

            if (string.IsNullOrEmpty(connectionName) || string.IsNullOrEmpty(containerName))
            {
                return new ValueTask<ConversionResult>(ConversionResult.Unhandled());
            }

            var connectionString = connectionName is null ? null : Environment.GetEnvironmentVariable(connectionName);
            var result = ToTargetType(TargetType, connectionString, containerName, blobName).GetAwaiter().GetResult();

            return result;
        }

        private IEnumerable<object> ConvertCollectionModelBindingDataAsync(Type TargetType, CollectionModelBindingData collectionModelBindingData)
        {
            var collectionblob = new List<object>();

            foreach(ModelBindingData modelBindingData in collectionModelBindingData.modelBindingDataArray)
            {
                var element = ConvertModelBindingDataAsync(modelBindingData, TargetType.GenericTypeArguments[0]);
                if (element != null)
                {
                    collectionblob.Add(element);
                }
            }

            var result = ToTargetCollectionType(TargetType.GenericTypeArguments[0], collectionblob);

            return result;
        }

        private bool TryGetBindingDataContent(ModelBindingData bindingData, out IDictionary<string, string> bindingDataContent)
        {
            bindingDataContent = bindingData.ContentType switch
            {
                Constants.JsonContentType => new Dictionary<string, string>(bindingData.Content.ToObjectFromJson<Dictionary<string, string>>(), StringComparer.OrdinalIgnoreCase),
                _ => null
            };

            if (bindingDataContent is null)
            {
                return false;
            }

            return true;
        }

        private async Task<object?> ToTargetType(Type targetType, string connectionString, string containerName, string blobName) => targetType switch
        {
            Type _ when targetType == typeof(String) => await GetBlobString(connectionString, containerName, blobName),
            Type _ when targetType == typeof(Stream) => await GetBlobStream(connectionString, containerName, blobName),
            Type _ when targetType == typeof(Byte[]) => await GetBlobBinaryData(connectionString, containerName, blobName),
            Type _ when targetType == typeof(BlobClient) => CreateBlobReference<BlobClient>(connectionString, containerName, blobName),
            Type _ when targetType == typeof(BlockBlobClient) => CreateBlobReference<BlockBlobClient>(connectionString, containerName, blobName),
            Type _ when targetType == typeof(PageBlobClient) => CreateBlobReference<PageBlobClient>(connectionString, containerName, blobName),
            Type _ when targetType == typeof(AppendBlobClient) => CreateBlobReference<AppendBlobClient>(connectionString, containerName, blobName),
            Type _ when targetType == typeof(BlobContainerClient) => CreateBlobContainerClient(connectionString, containerName),
            _ => await DeserializeToTargetObject(targetType, connectionString, containerName, blobName)
        };

        private IEnumerable<object> ToTargetCollectionType(Type targetType, IEnumerable<object> array) => targetType switch
        {
            Type _ when targetType == typeof(BlobClient) => array.Select(p=>(BlobClient)p),
            _ =>  throw new Exception()
        };

        private async Task<object?> DeserializeToTargetObject(Type targetType, string connectionString, string containerName, string blobName)
        {
            var content = await GetBlobString(connectionString, containerName, blobName);
            return JsonConvert.DeserializeObject(content, targetType);
        }

        private async Task<string> GetBlobString(string connectionString, string containerName, string blobName)
        {
            var client = CreateBlobReference<BlobClient>(connectionString, containerName, blobName);
            var download = await client.DownloadContentAsync();
            return download.Value.Content.ToString();
        }

        private async Task<Byte[]> GetBlobBinaryData(string connectionString, string containerName, string blobName)
        {
            using (MemoryStream stream = new MemoryStream())
            {
                var client = CreateBlobReference<BlobClient>(connectionString, containerName, blobName);
                await client.DownloadToAsync(stream);
                return stream.ToArray();
            }
        }

        private async Task<Stream> GetBlobStream(string connectionString, string containerName, string blobName)
        {
            var client = CreateBlobReference<BlobClient>(connectionString, containerName, blobName);
            var download = await client.DownloadStreamingAsync();
            return download.Value.Content;
        }

        private BlobContainerClient CreateBlobContainerClient(string connectionString, string containerName)
        {
            BlobContainerClient container = new(connectionString, containerName);
            return container;
        }

        private T CreateBlobReference<T>(string connectionString, string containerName, string blobName) where T : BlobBaseClient
        {
            if (string.IsNullOrEmpty(blobName))
            {
                throw new ArgumentNullException(nameof(blobName));
            }

            Type targetType = typeof(T);
            var container = CreateBlobContainerClient(connectionString, containerName);

            BlobBaseClient blob = targetType switch
            {
                Type _ when targetType == typeof(BlobClient) => container.GetBlobClient(blobName),
                Type _ when targetType == typeof(BlockBlobClient) => container.GetBlockBlobClient(blobName),
                Type _ when targetType == typeof(PageBlobClient) => container.GetPageBlobClient(blobName),
                Type _ when targetType == typeof(AppendBlobClient) => container.GetAppendBlobClient(blobName),
                _ => new(connectionString, containerName, blobName)
            };

            return (T)blob;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Anotar.NLog;
using Clowd.Server.Config;
using Clowd.Server.Util;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;

namespace Clowd.Server
{
    public class AzureStorageClient
    {
        public static AzureStorageClient Current;
        public string Endpoint { get; private set; }

        public CloudBlobContainer Public => _containers[ModelTypes.AzureContainer.Public];
        public CloudBlobContainer Private => _containers[ModelTypes.AzureContainer.Private];
        public CloudBlobContainer this[ModelTypes.AzureContainer container] => _containers[container];

        private Dictionary<ModelTypes.AzureContainer, CloudBlobContainer> _containers;
        private CloudStorageAccount _account;
        private CloudBlobClient _client;
        public AzureStorageClient(string configName = "default")
        {
            var config = ConfigurationManager.GetSection("azureEndpoints") as AzureEndpointSection;
            var endpoint = config.Instances[configName];
            var storageString = ConfigurationManager.ConnectionStrings[endpoint.ConnectionStringName].ConnectionString;

            Endpoint = endpoint.Endpoint;
            Current = this;

            _account = CloudStorageAccount.Parse(storageString);
            _client = _account.CreateCloudBlobClient();
            _containers = new Dictionary<ModelTypes.AzureContainer, CloudBlobContainer>();

            CloudBlobContainer priv = _client.GetContainerReference("private");
            if (!priv.Exists())
                LogTo.Error("Azure container does not exist: private");
            _containers.Add(ModelTypes.AzureContainer.Private, priv);

            CloudBlobContainer publ = _client.GetContainerReference("public");
            if (!publ.Exists())
                LogTo.Error("Azure container does not exist: private");

            _containers.Add(ModelTypes.AzureContainer.Public, publ);
        }

        public CloudBlockBlob GetBlob(ModelTypes.AzureContainer container, string storageKey)
        {
            return this[container].GetBlockBlobReference(storageKey);
        }
    }

    public static class AzureExtensions
    {
        public static string GetPublicAccessUrl(this CloudBlockBlob blob, SharedAccessBlobHeaders headers = null, TimeSpan? validFor = null)
        {
            var accessPolicy = new SharedAccessBlobPolicy()
            {
                Permissions = SharedAccessBlobPermissions.Read,
                SharedAccessExpiryTime = DateTime.UtcNow.Add(validFor ?? TimeSpan.FromHours(1)),
                SharedAccessStartTime = DateTime.UtcNow.AddMinutes(-5)
            };
            string sasBlobToken = headers != null
                ? blob.GetSharedAccessSignature(accessPolicy, headers)
                : blob.GetSharedAccessSignature(accessPolicy);

            var uri = new Uri(AzureStorageClient.Current.Endpoint).Append(blob.Uri.AbsolutePath + sasBlobToken).AbsoluteUri;
            return uri;
        }
    }
}
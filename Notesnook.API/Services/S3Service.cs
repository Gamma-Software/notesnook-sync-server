/*
This file is part of the Notesnook Sync Server project (https://notesnook.com/)

Copyright (C) 2023 Streetwriters (Private) Limited

This program is free software: you can redistribute it and/or modify
it under the terms of the Affero GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
Affero GNU General Public License for more details.

You should have received a copy of the Affero GNU General Public License
along with this program.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;
using Notesnook.API.Interfaces;
using Notesnook.API.Models;
using Streetwriters.Common;

namespace Notesnook.API.Services
{
    enum S3ClientMode
    {
        INTERNAL = 0,
        EXTERNAL = 1
    }

    public class S3Service : IS3Service
    {
        private readonly string BUCKET_NAME = Constants.S3_BUCKET_NAME ?? "";
        private readonly string INTERNAL_BUCKET_NAME = Constants.S3_INTERNAL_BUCKET_NAME ?? "";
        private AmazonS3Client S3Client { get; }

        // When running in a dockerized environment the sync server doesn't have access
        // to the host's S3 Service URL. It can only talk to S3 server via its own internal
        // network. This creates the issue where the client needs host-level access while
        // the sync server needs only internal access.
        // This wouldn't be a big issue (just map one to the other right?) but the signed
        // URLs generated by S3 are host specific. Changing their hostname on the fly causes
        // SignatureDoesNotMatch error.
        // That is why we create 2 separate S3 clients. One for internal traffic and one for external.
        private AmazonS3Client S3InternalClient { get; }
        private HttpClient httpClient = new HttpClient();

        public S3Service()
        {
            var config = new AmazonS3Config
            {
#if (DEBUG || STAGING)
                ServiceURL = Servers.S3Server.ToString(),
#else
                ServiceURL = Constants.S3_SERVICE_URL,
                AuthenticationRegion = Constants.S3_REGION,
#endif
                ForcePathStyle = true,
                SignatureMethod = SigningAlgorithm.HmacSHA256,
                SignatureVersion = "4"
            };
#if (DEBUG || STAGING)
            S3Client = new AmazonS3Client("S3RVER", "S3RVER", config);
#else
            S3Client = new AmazonS3Client(Constants.S3_ACCESS_KEY_ID, Constants.S3_ACCESS_KEY, config);
#endif

            if (!string.IsNullOrEmpty(Constants.S3_INTERNAL_SERVICE_URL))
            {
                S3InternalClient = new AmazonS3Client(Constants.S3_ACCESS_KEY_ID, Constants.S3_ACCESS_KEY, new AmazonS3Config
                {
                    ServiceURL = Constants.S3_INTERNAL_SERVICE_URL,
                    AuthenticationRegion = Constants.S3_REGION,
                    ForcePathStyle = true,
                    SignatureMethod = SigningAlgorithm.HmacSHA256,
                    SignatureVersion = "4"
                });
            }

            AWSConfigsS3.UseSignatureVersion4 = true;
        }

        public async Task DeleteObjectAsync(string userId, string name)
        {
            var objectName = GetFullObjectName(userId, name);
            if (objectName == null) throw new Exception("Invalid object name."); ;

            var response = await GetS3Client(S3ClientMode.INTERNAL).DeleteObjectAsync(GetBucketName(S3ClientMode.INTERNAL), objectName);

            if (!IsSuccessStatusCode(((int)response.HttpStatusCode)))
                throw new Exception("Could not delete object.");
        }

        public async Task DeleteDirectoryAsync(string userId)
        {
            var request = new ListObjectsV2Request
            {
                BucketName = GetBucketName(S3ClientMode.INTERNAL),
                Prefix = userId,
            };

            var response = new ListObjectsV2Response();
            var keys = new List<KeyVersion>();
            do
            {
                response = await GetS3Client(S3ClientMode.INTERNAL).ListObjectsV2Async(request);
                response.S3Objects.ForEach(obj => keys.Add(new KeyVersion
                {
                    Key = obj.Key,
                }));

                request.ContinuationToken = response.NextContinuationToken;
            }
            while (response.IsTruncated);

            if (keys.Count <= 0) return;

            var deleteObjectsResponse = await GetS3Client(S3ClientMode.INTERNAL)
            .DeleteObjectsAsync(new DeleteObjectsRequest
            {
                BucketName = GetBucketName(S3ClientMode.INTERNAL),
                Objects = keys,
            });

            if (!IsSuccessStatusCode((int)deleteObjectsResponse.HttpStatusCode))
                throw new Exception("Could not delete directory.");
        }

        public async Task<long> GetObjectSizeAsync(string userId, string name)
        {
            var url = this.GetPresignedURL(userId, name, HttpVerb.HEAD, S3ClientMode.INTERNAL);
            if (url == null) return 0;

            var request = new HttpRequestMessage(HttpMethod.Head, url);
            var response = await httpClient.SendAsync(request);
            const long MAX_SIZE = 513 * 1024 * 1024; // 512 MB
            if (!Constants.IS_SELF_HOSTED && response.Content.Headers.ContentLength >= MAX_SIZE)
            {
                await this.DeleteObjectAsync(userId, name);
                throw new Exception("File size exceeds the maximum allowed size.");
            }
            return response.Content.Headers.ContentLength ?? 0;
        }


        public string GetUploadObjectUrl(string userId, string name)
        {
            var url = this.GetPresignedURL(userId, name, HttpVerb.PUT);
            if (url == null) return null;
            return url;
        }

        public string GetDownloadObjectUrl(string userId, string name)
        {
            var url = this.GetPresignedURL(userId, name, HttpVerb.GET);
            if (url == null) return null;
            return url;
        }

        public async Task<MultipartUploadMeta> StartMultipartUploadAsync(string userId, string name, int parts, string uploadId = null)
        {
            var objectName = GetFullObjectName(userId, name);
            if (userId == null || objectName == null) throw new Exception("Could not initiate multipart upload.");

            if (string.IsNullOrEmpty(uploadId))
            {
                var response = await GetS3Client(S3ClientMode.INTERNAL).InitiateMultipartUploadAsync(GetBucketName(S3ClientMode.INTERNAL), objectName);
                if (!IsSuccessStatusCode(((int)response.HttpStatusCode))) throw new Exception("Failed to initiate multipart upload.");

                uploadId = response.UploadId;
            }

            var signedUrls = new string[parts];
            for (var i = 0; i < parts; ++i)
            {
                signedUrls[i] = GetPresignedURLForUploadPart(objectName, uploadId, i + 1);
            }

            return new MultipartUploadMeta
            {
                UploadId = uploadId,
                Parts = signedUrls
            };
        }

        public async Task AbortMultipartUploadAsync(string userId, string name, string uploadId)
        {
            var objectName = GetFullObjectName(userId, name);
            if (userId == null || objectName == null) throw new Exception("Could not abort multipart upload.");

            var response = await GetS3Client(S3ClientMode.INTERNAL).AbortMultipartUploadAsync(GetBucketName(S3ClientMode.INTERNAL), objectName, uploadId);
            if (!IsSuccessStatusCode(((int)response.HttpStatusCode))) throw new Exception("Failed to abort multipart upload.");
        }

        public async Task CompleteMultipartUploadAsync(string userId, CompleteMultipartUploadRequest uploadRequest)
        {
            var objectName = GetFullObjectName(userId, uploadRequest.Key);
            if (userId == null || objectName == null) throw new Exception("Could not abort multipart upload.");

            uploadRequest.Key = objectName;
            uploadRequest.BucketName = GetBucketName(S3ClientMode.INTERNAL);
            var response = await GetS3Client(S3ClientMode.INTERNAL).CompleteMultipartUploadAsync(uploadRequest);
            if (!IsSuccessStatusCode(((int)response.HttpStatusCode))) throw new Exception("Failed to complete multipart upload.");
        }

        private string GetPresignedURL(string userId, string name, HttpVerb httpVerb, S3ClientMode mode = S3ClientMode.EXTERNAL)
        {
            var objectName = GetFullObjectName(userId, name);
            if (userId == null || objectName == null) return null;

            var client = GetS3Client(mode);
            var request = new GetPreSignedUrlRequest
            {
                BucketName = GetBucketName(mode),
                Expires = System.DateTime.Now.AddHours(1),
                Verb = httpVerb,
                Key = objectName,
#if (DEBUG || STAGING)
                Protocol = Protocol.HTTP,
#else
                Protocol = client.Config.ServiceURL.StartsWith("http://") ? Protocol.HTTP : Protocol.HTTPS,
#endif
            };
            return client.GetPreSignedURL(request);
        }

        private string GetPresignedURLForUploadPart(string objectName, string uploadId, int partNumber)
        {

            var client = GetS3Client(S3ClientMode.INTERNAL);
            return client.GetPreSignedURL(new GetPreSignedUrlRequest
            {
                BucketName = GetBucketName(S3ClientMode.INTERNAL),
                Expires = System.DateTime.Now.AddHours(1),
                Verb = HttpVerb.PUT,
                Key = objectName,
                PartNumber = partNumber,
                UploadId = uploadId,
#if (DEBUG || STAGING)
                Protocol = Protocol.HTTP,
#else
                Protocol = client.Config.ServiceURL.StartsWith("http://") ? Protocol.HTTP : Protocol.HTTPS,
#endif
            });
        }

        private string GetFullObjectName(string userId, string name)
        {
            if (userId == null || !Regex.IsMatch(name, "[0-9a-zA-Z!" + Regex.Escape("-") + "_.*'()]")) return null;
            return $"{userId}/{name}";
        }

        bool IsSuccessStatusCode(int statusCode)
        {
            return ((int)statusCode >= 200) && ((int)statusCode <= 299);
        }

        AmazonS3Client GetS3Client(S3ClientMode mode = S3ClientMode.EXTERNAL)
        {
            if (mode == S3ClientMode.INTERNAL && S3InternalClient != null) return S3InternalClient;
            return S3Client;
        }

        string GetBucketName(S3ClientMode mode = S3ClientMode.EXTERNAL)
        {
            if (mode == S3ClientMode.INTERNAL && S3InternalClient != null) return INTERNAL_BUCKET_NAME;
            return BUCKET_NAME;
        }
    }
}
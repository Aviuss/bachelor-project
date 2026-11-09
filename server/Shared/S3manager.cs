using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Transfer;

namespace Shared;

public class S3manager
{
    private readonly string bucket;
    private readonly TransferUtility transfer;

    public IAmazonS3 Client { get; }

    public S3manager(string endpoint, string region, string accessKey, string secretKey, string bucket)
    {
        var credentials = new BasicAWSCredentials(accessKey, secretKey);
        var config = new AmazonS3Config
        {
            ServiceURL = endpoint,
            AuthenticationRegion = region,
            ForcePathStyle = true,
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        };
        Client = new AmazonS3Client(credentials, config);
        transfer = new TransferUtility(Client);
        this.bucket = bucket;
    }

    public Task downloadResource(string key, string locationTo, string name) =>
        transfer.DownloadAsync(Path.Combine(locationTo, name), bucket, key);

    public Task writeResource(string key, string locationFrom, string name) =>
        transfer.UploadAsync(Path.Combine(locationFrom, name), bucket, key);

    public Task deleteResource(string key) =>
        Client.DeleteObjectAsync(bucket, key);
}

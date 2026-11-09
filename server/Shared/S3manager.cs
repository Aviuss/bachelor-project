using Amazon.Runtime;
using Amazon.S3;

namespace Shared;

public class S3manager
{
    public IAmazonS3 Client { get; }

    public S3manager(string endpoint, string region, string accessKey, string secretKey)
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
    }
}

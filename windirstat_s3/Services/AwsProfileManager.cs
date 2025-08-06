using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Amazon;
using Amazon.Runtime;
using Amazon.Runtime.CredentialManagement;

namespace windirstat_s3.Services;

public class AwsProfileManager
{
    private readonly SharedCredentialsFile _sharedFile;

    public AwsProfileManager()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".aws",
            "credentials");
        _sharedFile = new SharedCredentialsFile(path);
    }

    public IEnumerable<string> ListProfiles()
    {
        return _sharedFile.ListProfiles().Select(p => p.Name);
    }

    public AWSCredentials GetCredentials(string profileName)
    {
        if (_sharedFile.TryGetProfile(profileName, out var profile) &&
            AWSCredentialsFactory.TryGetAWSCredentials(profile, _sharedFile, out var credentials))
        {
            return credentials;
        }

        throw new InvalidOperationException($"Profile '{profileName}' not found.");
    }

    public RegionEndpoint GetRegion(string profileName)
    {
        if (_sharedFile.TryGetProfile(profileName, out var profile) && profile.Region != null)
        {
            return profile.Region;
        }

        throw new InvalidOperationException($"Profile '{profileName}' not found or has no region.");
    }

    public void SaveProfile(string profileName, string accessKey, string secretKey, RegionEndpoint region)
    {
        var options = new CredentialProfileOptions
        {
            AccessKey = accessKey,
            SecretKey = secretKey
        };

        var profile = new CredentialProfile(profileName, options)
        {
            Region = region
        };

        _sharedFile.RegisterProfile(profile);
    }
}

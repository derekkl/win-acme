﻿using Amazon;
using Amazon.Route53;
using Amazon.Route53.Model;
using Amazon.Runtime;
using PKISharp.WACS.Clients.DNS;
using PKISharp.WACS.Services;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace PKISharp.WACS.Plugins.ValidationPlugins.Dns
{
    internal sealed class Route53 : DnsValidation<Route53>
    {
        private readonly IAmazonRoute53 _route53Client;
        private readonly DomainParseService _domainParser;

        public Route53(
            LookupClientProvider dnsClient,
            DomainParseService domainParser,
            ILogService log,
            ProxyService proxy,
            ISettingsService settings,
            Route53Options options)
            : base(dnsClient, log, settings)
        {
            var region = RegionEndpoint.USEast1;
            var config = new AmazonRoute53Config() { RegionEndpoint = region };
            config.SetWebProxy(proxy.GetWebProxy());
            _route53Client = !string.IsNullOrWhiteSpace(options.IAMRole)
                ? new AmazonRoute53Client(new InstanceProfileAWSCredentials(options.IAMRole), config)
                : !string.IsNullOrWhiteSpace(options.AccessKeyId) && !string.IsNullOrWhiteSpace(options.SecretAccessKey.Value)
                    ? new AmazonRoute53Client(options.AccessKeyId, options.SecretAccessKey.Value, config)
                    : new AmazonRoute53Client(config);
            _domainParser = domainParser;
        }

        private static ResourceRecordSet CreateResourceRecordSet(string name, string value)
        {
            return new ResourceRecordSet
            {
                Name = name,
                Type = RRType.TXT,
                ResourceRecords = new List<ResourceRecord> {
                    new ResourceRecord("\"" + value + "\"") },
                TTL = 1L
            };
        }

        public override async Task CreateRecord(string recordName, string token)
        {
            var hostedZoneId = await GetHostedZoneId(recordName);
            if (hostedZoneId != null)
            {
                _log.Information($"Creating TXT record {recordName} with value {token}");
                var response = await _route53Client.ChangeResourceRecordSetsAsync(
                    new ChangeResourceRecordSetsRequest(
                        hostedZoneId,
                        new ChangeBatch(new List<Change> {
                            new Change(
                                ChangeAction.UPSERT, 
                                CreateResourceRecordSet(recordName, token))
                        })));
                await WaitChangesPropagation(response.ChangeInfo);
            }
        }

        public override async Task DeleteRecord(string recordName, string token)
        {
            var hostedZoneId = await GetHostedZoneId(recordName);
            if (hostedZoneId != null)
            {
                _log.Information($"Deleting TXT record {recordName} with value {token}");
                _ = await _route53Client.ChangeResourceRecordSetsAsync(
                    new ChangeResourceRecordSetsRequest(hostedZoneId,
                        new ChangeBatch(new List<Change> {
                            new Change(
                                ChangeAction.DELETE,
                                CreateResourceRecordSet(recordName, token))
                        })));
            }
        }

        private async Task<string> GetHostedZoneId(string recordName)
        {
            var domainName = _domainParser.GetDomain(recordName);
            var hostedZones = new List<HostedZone>();
            var response = await _route53Client.ListHostedZonesAsync();
            hostedZones.AddRange(response.HostedZones);
            while (response.IsTruncated)
            {
                response = await _route53Client.ListHostedZonesAsync(
                    new ListHostedZonesRequest() { 
                        Marker = response.NextMarker 
                    });
                hostedZones.AddRange(response.HostedZones);
            }
            _log.Debug("Found {count} hosted zones in AWS", hostedZones);
       
            var hostedZone = hostedZones.Select(zone =>
            {
                var fit = 0;
                var name = zone.Name.TrimEnd('.').ToLowerInvariant();
                if (recordName.ToLowerInvariant().EndsWith(name))
                {
                    // If there is a zone for a.b.c.com (4) and one for c.com (2)
                    // then the former is a better (more specific) match than the 
                    // latter, so we should use that
                    fit = name.Split('.').Count();
                    _log.Verbose("Zone {name} scored {fit} points", zone.Name, fit);
                } 
                else
                {
                    _log.Verbose("Zone {name} not matched", zone.Name);
                }
                return new { zone, fit };
            }).
            Where(x => x.fit > 0).
            OrderByDescending(x => x.fit).
            FirstOrDefault();

            if (hostedZone != null)
            {
                return hostedZone.zone.Id;
            }

            _log.Error($"Can't find hosted zone for domain {domainName}");
            return null;
        }

        private async Task WaitChangesPropagation(ChangeInfo changeInfo)
        {
            if (changeInfo.Status == ChangeStatus.INSYNC)
            {
                return;
            }

            _log.Information("Waiting for DNS changes propagation");

            var changeRequest = new GetChangeRequest(changeInfo.Id);

            while ((await _route53Client.GetChangeAsync(changeRequest)).ChangeInfo.Status == ChangeStatus.PENDING)
            {
                await Task.Delay(5000);
            }
        }
    }
}
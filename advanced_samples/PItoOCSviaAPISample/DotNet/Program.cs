// <copyright file="Program.cs" company="OSIsoft, LLC">
//
//Copyright 2019 OSIsoft, LLC
//
//Licensed under the Apache License, Version 2.0 (the "License");
//you may not use this file except in compliance with the License.
//You may obtain a copy of the License at
//
//<http://www.apache.org/licenses/LICENSE-2.0>
//
//Unless required by applicable law or agreed to in writing, software
//distributed under the License is distributed on an "AS IS" BASIS,
//WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//See the License for the specific language governing permissions and
//limitations under the License.
// </copyright>

using System;
using System.Collections.Generic;
using System.Linq;
using CommandLine;
using OSIsoft.AF.Asset;
using OSIsoft.AF.Data;
using OSIsoft.AF.PI;
using OSIsoft.Data;
using OSIsoft.Data.Http.Security;
using OSIsoft.AF.Time;
using System.Configuration;
using System.Net;
using System.Threading.Tasks;
using OSIsoft.Data.Http;

namespace PItoOCSviaAPISample
{
    class Program
    {
        private static CommandLineOptions _options; 

        static int Main(string[] args)
        {
            _options = new CommandLineOptions();
            var errors = new List<Error>();
            var result = Parser.Default.ParseArguments<CommandLineOptions>(args);
            result.WithParsed(opts => _options = opts).WithNotParsed(errs => errors = errs.ToList());
            if (errors.Any())
            {
                foreach (var error in errors)
                {
                    Console.WriteLine(error.Tag);
                }

                return 1;
            }
            
            MainAsync().Wait();
            return 0;
        }

        private static async Task MainAsync()
        {
            string accountId = ConfigurationManager.AppSettings["accountId"];
            string namespaceId = ConfigurationManager.AppSettings["namespaceId"];
            string address = ConfigurationManager.AppSettings["address"];
            string resource = ConfigurationManager.AppSettings["resource"];
            string clientId = ConfigurationManager.AppSettings["clientId"];
            string clientSecret = ConfigurationManager.AppSettings["clientSecret"];
            string piServerName = ConfigurationManager.AppSettings["PIDataArchive"];

            var sdsService = new SdsService(new Uri(address),
                new SdsSecurityHandler(resource, accountId, clientId, clientSecret));

            var metadataService = sdsService.GetMetadataService(accountId, namespaceId);
            var dataService = sdsService.GetDataService(accountId, namespaceId);

            var piServer = new PIServers()[piServerName];
            piServer.Connect();

            PIPointQuery nameFilter = new PIPointQuery
            {
                AttributeName = PICommonPointAttributes.Tag,
                AttributeValue = _options.TagMask,
                Operator = OSIsoft.AF.Search.AFSearchOperator.Equal
            };

            IEnumerable<string> attributesToRetrieve = new[]
            {
                PICommonPointAttributes.Descriptor,
                PICommonPointAttributes.EngineeringUnits,
                PICommonPointAttributes.PointSource
            };

            var piPoints =
                (await PIPoint.FindPIPointsAsync(piServer, new[] {nameFilter}, attributesToRetrieve)).ToList();

            if (!piPoints.Any())
            {
                Console.WriteLine($"No points found matching the tagMask query!");
                return;
            }
            Console.WriteLine($"Found {piPoints.Count} points matching mask: {_options.TagMask}");

            //create types
            await PISdsTypes.CreateOrUpdateTypesInOcsAsync(metadataService);

            //delete existing streams if requested
            if (_options.Mode == CommandLineOptions.DataWriteModes.clearExistingData)
            {
                Parallel.ForEach(piPoints, piPoint => DeleteStreamBasedOnPIPointAsync(piPoint, metadataService).Wait());
            }

            Parallel.ForEach(piPoints, piPoint => CreateStreamBasedOnPIPointAsync(piPoint, attributesToRetrieve, metadataService).Wait());
            Console.WriteLine($"Created or updated {piPoints.Count()} streams.");

            //for each PIPoint, get the data of interest and write it to OCS
            Parallel.ForEach(piPoints, piPoint =>
            {
                //Indices must be unique in OCS so we get rid of duplicate values for a given timestamp
                var timeRange = new AFTimeRange(_options.StartTime, _options.EndTime);
                var afValues = piPoint.RecordedValues(timeRange, AFBoundaryType.Inside, null, true)
                    .GroupBy(value => value.Timestamp)
                    .Select(valuesAtTimestamp => valuesAtTimestamp.Last()) //last event for particular timestamp
                    .Where(val => val.IsGood) //System Digital States (e.g. Shutdown, IO Timeout, etc...) are ignored
                    .ToList();

                WriteDataToOcsAsync(piPoint, afValues, dataService).Wait();
            });
        }

        private static string GetStreamId(PIPoint point)
        {
            /*Enforcing the rules for Stream ID
               Is not case sensitive.
               Can contain spaces.
               Cannot start with two underscores (�__�).
               Can contain a maximum of 260 characters.
               Cannot use the following characters: ( / : ? # [ ] @ ! $ & � ( ) \* + , ; = %)
               Cannot start or end with a period.
               Cannot contain consecutive periods.
               Cannot consist of only periods.
             */
            var result = $"PI_{point.Server.Name}_{point.Name}";
            if (result.Length > 260)
            {
                result = result.Substring(0, 260);
            }

            const string forbiddenChars = @"/:?#[]@!$&�()\*+,;=%";
            if (result.EndsWith(@"."))
            {
                result = result.TrimEnd('.');
            }

            if (result.Contains(".."))
            {
                result = result.Replace("..", "_");
            }

            foreach (var forbiddenChar in forbiddenChars)
            {
                if (result.Contains(forbiddenChar))
                {
                    result = result.Replace(forbiddenChar, '_');
                }
            }

            return result;
        }
        
        private static async Task DeleteStreamBasedOnPIPointAsync(PIPoint piPoint, ISdsMetadataService metadata)
        {
            var id = GetStreamId(piPoint);
            try
            {
                await metadata.GetStreamAsync(id);
            }
            catch (SdsHttpClientException ex)
            {
                if (ex.StatusCode == HttpStatusCode.NotFound)
                {
                    Console.WriteLine($"Stream to be deleted not found: {id}.");
                    return;
                }

                throw;
            }

            await metadata.DeleteStreamAsync(id);
            Console.WriteLine($"Deleted stream {id}");
        }
        
        private static async Task CreateStreamBasedOnPIPointAsync(PIPoint piPoint,
            IEnumerable<string> pointAttributes, ISdsMetadataService metadata)
        {
            var otherAttributes = pointAttributes.Where(s => s != PICommonPointAttributes.Descriptor)
                .ToDictionary(s => s, s => piPoint.GetAttribute(s).ToString());

            var id = GetStreamId(piPoint);
            var dataType = PISdsTypes.GetDataType(piPoint.PointType);

            await metadata.CreateOrUpdateStreamAsync(new SdsStream()
            {
                Id = id,
                Name = piPoint.Name,
                TypeId = PISdsTypes.GetSdsTypeId(dataType),
                Description = piPoint.GetAttribute(PICommonPointAttributes.Descriptor).ToString()
            });

            //write stream metadata from PIPoint attributes
            await metadata.UpdateStreamMetadataAsync(id, otherAttributes);
        }

        private static async Task WriteDataToOcsAsync(PIPoint piPoint, List<AFValue> afValues, ISdsDataService data)
        {
            var streamId = GetStreamId(piPoint);

            switch (PISdsTypes.GetDataType(piPoint.PointType))
            {
                case StreamDataType.Integer:
                    await WriteDataForIntegerStreamAsync(data, afValues, streamId);
                    break;
                case StreamDataType.Float:
                    await WriteDataForFloatStreamAsync(data, afValues, streamId);
                    break;
                case StreamDataType.String:
                    await WriteDataForStringStreamAsync(data, afValues, streamId);
                    break;
                case StreamDataType.Blob:
                    await WriteDataForBlobStreamAsync(data, afValues, streamId);
                    break;
                case StreamDataType.Time:
                    await WriteDataForTimeStreamAsync(data, afValues, streamId);
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }

            Console.WriteLine(
                $"Writing data for point: {piPoint.Name} to stream {streamId} ({afValues.Count} values written.)");
        }

        private static async Task WriteDataForIntegerStreamAsync(ISdsDataService data, List<AFValue> afValues, string streamId)
        {
            var dataList = new List<PISdsTypes.IntegerData>();
            dataList.AddRange(afValues.Select(val => new PISdsTypes.IntegerData()
            {
                Timestamp = val.Timestamp,
                Value = val.ValueAsInt32()
            }));
            await data.UpdateValuesAsync(streamId, dataList);
        }

        private static async Task WriteDataForFloatStreamAsync(ISdsDataService data, List<AFValue> afValues, string streamId)
        {
            var dataList = afValues.Select(val => new PISdsTypes.DoubleData()
            {
                Timestamp = val.Timestamp,
                Value = val.ValueAsDouble()
            }).ToList();
            await data.UpdateValuesAsync(streamId, dataList);
        }

        private static async Task WriteDataForStringStreamAsync(ISdsDataService data, List<AFValue> afValues, string streamId)
        {
            var dataList = afValues.Select(val => new PISdsTypes.StringData()
            {
                Timestamp = val.Timestamp,
                Value = val.Value.ToString()
            }).ToList();
            await data.UpdateValuesAsync(streamId, dataList);
        }

        private static async Task WriteDataForBlobStreamAsync(ISdsDataService data, List<AFValue> afValues, string streamId)
        {
            var dataList = afValues.Select(val => new PISdsTypes.BlobData()
            {
                Timestamp = val.Timestamp,
                Value = (byte[]) val.Value
            }).ToList();
            await data.UpdateValuesAsync(streamId, dataList);
        }

        private static async Task WriteDataForTimeStreamAsync(ISdsDataService data, List<AFValue> afValues, string streamId)
        {
            var dataList = afValues.Select(val => new PISdsTypes.TimeData()
            {
                Timestamp = val.Timestamp,
                Value = (DateTime) val.Value
            }).ToList();
            await data.UpdateValuesAsync(streamId, dataList);
        }
    }
}



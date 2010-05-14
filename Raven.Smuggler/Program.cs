﻿using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Raven.Smuggler
{
    class Program
    {
        static void Main(string[] args)
        {
            if (args.Length != 3 || args[0] != "in" && args[0] != "out")
            {
                Console.WriteLine(@"
Raven Smuggler - Import/Export utility
Usage:
    - Import the dump.raven file to a local instance:
        Raven.Smuggler in http://localhost:8080/ dump.raven
    - Export a local instance to dump.raven:
        Raven.Smuggle out http://localhost:8080/ dump.raven
");
            }

            try
            {
                var instanceUrl = args[1];
                if (instanceUrl.EndsWith("/") == false)
                    instanceUrl += "/";
                var file = args[2];
                switch (args[0])
                {
                    case "in":
                        ImportData(instanceUrl, file);
                        break;
                    case "out":
                        ExportData(instanceUrl, file);
                        break;
                }
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                Environment.Exit(-1);
            }
        }

        private static void ExportData(string instanceUrl, string file)
        {
            using (var streamWriter = new StreamWriter(new GZipStream(File.Create(file), CompressionMode.Compress)))
            {
                var jsonWriter = new JsonTextWriter(streamWriter)
                {
                    Formatting = Formatting.Indented
                };
                jsonWriter.WriteStartObject();
                jsonWriter.WritePropertyName("Indexes");
                jsonWriter.WriteStartArray();
                using (var webClient = new WebClient())
                {
                    int totalCount = 0;
                    while (true)
                    {
                        var documents = webClient.DownloadString(instanceUrl + "indexes?pageSize=128&start=" + totalCount);
                        var array = JArray.Parse(documents);
                        if (array.Count == 0)
                        {
                            Console.WriteLine("Done with reading indexes, total: {0}", totalCount);
                            break;
                        }
                        totalCount += array.Count;
                        Console.WriteLine("Reading batch of {0,3} indexes, read so far: {1,10:#,#}", array.Count,
                                          totalCount);
                        foreach (JToken item in array)
                        {
                            item.WriteTo(jsonWriter);
                        }
                    }
                }
                jsonWriter.WriteEndArray();
                jsonWriter.WritePropertyName("Docs");
                jsonWriter.WriteStartArray();
                using (var webClient = new WebClient())
                {
                    var lastEtag = Guid.Empty;
                    int totalCount = 0;
                    while (true)
                    {
                        var documents = webClient.DownloadString(instanceUrl + "docs?pageSize=128&etag=" + lastEtag);
                        var array = JArray.Parse(documents);
                        if (array.Count == 0)
                        {
                            Console.WriteLine("Done with reading documents, total: {0}", totalCount);
                            break;
                        }
                        totalCount += array.Count;
                        Console.WriteLine("Reading batch of {0,3} documents, read so far: {1,10:#,#}", array.Count,
                                          totalCount);
                        foreach (JToken item in array)
                        {
                            item.WriteTo(jsonWriter);
                        }
                        lastEtag = new Guid(array.Last.Value<JObject>("@metadata").Value<string>("@etag"));
                    }
                }
                jsonWriter.WriteEndArray();
                jsonWriter.WriteEndObject();
                streamWriter.Flush();
            }
        }

        private static void ImportData(string instanceUrl, string file)
        {
            using (var streamReader = new StreamReader(new GZipStream(File.OpenRead(file), CompressionMode.Decompress)))
            {
                var jsonReader = new JsonTextReader(streamReader);
                if (jsonReader.Read() == false)
                    return;
                if (jsonReader.TokenType != JsonToken.StartObject)
                    throw new InvalidDataException("StartObject was expected");
                // should read indexes now
                if (jsonReader.Read() == false)
                    return;
                if (jsonReader.TokenType != JsonToken.PropertyName)
                    throw new InvalidDataException("PropertyName was expected");
                if (Equals("Indexes", jsonReader.Value) == false)
                    throw new InvalidDataException("Indexes property was expected");
                if (jsonReader.Read() == false)
                    return;
                if (jsonReader.TokenType != JsonToken.StartArray)
                    throw new InvalidDataException("StartArray was expected");
                using (var webClient = new WebClient())
                {
                    webClient.UseDefaultCredentials = true;
                    webClient.Credentials = CredentialCache.DefaultNetworkCredentials; 
                    while (jsonReader.Read() && jsonReader.TokenType != JsonToken.EndArray)
                    {
                        var index = JToken.ReadFrom(jsonReader);
                        var indexName = index.Value<string>("name");
                        if(indexName.StartsWith("Raven/"))
                            continue;
                        using (var streamWriter = new StreamWriter(webClient.OpenWrite(instanceUrl + "indexes/" + indexName, "PUT")))
                        using (var jsonTextWriter = new JsonTextWriter(streamWriter))
                        {
                            index.Value<JObject>("definition").WriteTo(jsonTextWriter);
                            jsonTextWriter.Flush();
                            streamWriter.Flush();
                        }
                    }
                }
                // should read documents now
                if (jsonReader.Read() == false)
                    return;
                if (jsonReader.TokenType != JsonToken.PropertyName)
                    throw new InvalidDataException("PropertyName was expected");
                if (Equals("Docs", jsonReader.Value) == false)
                    throw new InvalidDataException("Docs property was expected");
                if (jsonReader.Read() == false)
                    return;
                if (jsonReader.TokenType != JsonToken.StartArray)
                    throw new InvalidDataException("StartArray was expected");
                var batch = new List<JObject>();
                while (jsonReader.Read() && jsonReader.TokenType != JsonToken.EndArray)
                {
                    var document = JToken.ReadFrom(jsonReader);
                    batch.Add((JObject)document);
                    if (batch.Count > 128)
                        FlushBatch(instanceUrl, batch);
                }
                FlushBatch(instanceUrl, batch);
            }
        }

        private static void FlushBatch(string instanceUrl, List<JObject> batch)
        {
            using (var webClient = new WebClient())
            {
                webClient.UseDefaultCredentials = true;
                webClient.Credentials = CredentialCache.DefaultNetworkCredentials;
                using (var stream = webClient.OpenWrite(instanceUrl + "bulk_docs", "POST"))
                using (var streamWriter = new StreamWriter(stream))
                using (var jsonTextWriter = new JsonTextWriter(streamWriter))
                {
                    var commands = new JArray();
                    foreach (var doc in batch)
                    {
                        var metadata = doc.Value<JObject>("@metadata");
                        doc.Remove("@metadata");
                        commands.Add(new JObject(
                                         new JProperty("Method", "PUT"),
                                         new JProperty("Document", doc),
                                         new JProperty("Metadata", metadata),
                                         new JProperty("Key", metadata.Value<string>("@id"))
                                         ));
                    }
                    commands.WriteTo(jsonTextWriter);
                    jsonTextWriter.Flush();
                    streamWriter.Flush();
                }
            }
            batch.Clear();
        }
    }
}

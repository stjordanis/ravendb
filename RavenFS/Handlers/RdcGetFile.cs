﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Policy;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;
using RavenFS.Infrastructure;
using RavenFS.Util;
using RavenFS.Extensions;
using Rdc.Wrapper;

namespace RavenFS.Handlers
{
    [HandlerMetadata("^/rdc/files/(.+)", "GET")]
    public class RdcFileHandler : AbstractAsyncHandler
    {
        private static readonly Regex startRange = new Regex(@"^bytes=(\d+)-(\d+)?$", RegexOptions.Compiled);

        protected override Task ProcessRequestAsync(HttpContext context)
        {
            context.Response.BufferOutput = false;
            var fileName = Url.Match(context.Request.CurrentExecutionFilePath).Groups[1].Value;            

            var storageStream = new StorageStream(Storage, fileName);
            var range = GetRangeFromHeader(context);
            var from = range.Item1;
            var to = range.Item2 ?? storageStream.Length - 1;

            context.Response.AddHeader("Content-Length", (to - from + 1).ToString());
            context.Response.AddHeader("Content-Disposition", "attachment; filename=" + storageStream.Name);
            
            return storageStream.CopyToAsync(context.Response.OutputStream, from, to)
                .ContinueWith(task => storageStream.Dispose());
        }

        private static Tuple<long, long?> GetRangeFromHeader(HttpContext context)
        {
            var literal = context.Request.Headers["Range"];
            if (string.IsNullOrEmpty(literal))
                return null;

            var match = startRange.Match(literal);

            if (match.Success == false)
                return null;

            long from;
            long to;
            if (! long.TryParse(match.Groups[1].Value, out from))
            {
                return null;
            }
            if (long.TryParse(match.Groups[2].Value, out to))
            {
                return new Tuple<long, long?>(from, to);
            }
            return new Tuple<long, long?>(from, null);
        }
    }
}
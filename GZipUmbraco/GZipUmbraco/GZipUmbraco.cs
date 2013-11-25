using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Web;

[assembly: PreApplicationStartMethod(typeof(PreApplicationStartCode), "Start")]
public class GZipUmbraco : IHttpModule
{
    /*Code completely wripped off from http://madskristensen.net/post/http-compression-of-webresourceaxd-and-pages-in-aspnet*/

    public GZipUmbraco()
    {
    }

    public String ModuleName
    {
        get { return "GZipUmbraco"; }
    }

    #region IHttpModule Members

    /// <summary>
    /// Disposes of the resources (other than memory) used by the module 
    /// that implements <see cref="T:System.Web.IHttpModule"></see>.
    /// </summary>
    void IHttpModule.Dispose()
    {
        // Nothing to dispose; 
    }

    /// <summary>
    /// Initializes a module and prepares it to handle requests.
    /// </summary>
    /// <param name="context">An <see cref="T:System.Web.HttpApplication"></see> 
    /// that provides access to the methods, properties, and events common to 
    /// all application objects within an ASP.NET application.
    /// </param>
    void IHttpModule.Init(HttpApplication context)
    {
        // For page compression
        context.PreRequestHandlerExecute += new EventHandler(context_PostReleaseRequestState);

        //// For WebResource.axd compression - THIS BREAKS BACKEND OF UMBRACO - DO NOT COMMENT OUT
        //context.BeginRequest += new EventHandler(context_BeginRequest);
        //context.EndRequest += new EventHandler(context_EndRequest);
    }

    #endregion

    private const string GZIP = "gzip";
    private const string DEFLATE = "deflate";

    #region Compress page

    /// <summary>
    /// Handles the BeginRequest event of the context control.
    /// </summary>
    /// <param name="sender">The source of the event.</param>
    /// <param name="e">The <see cref="System.EventArgs"/> instance containing the event data.</param>
    void context_PostReleaseRequestState(object sender, EventArgs e)
    {
        HttpApplication app = (HttpApplication)sender;
        if (!app.Request.Path.StartsWith("/umbraco"))
        {
            if (app.Context.CurrentHandler is System.Web.UI.Page && app.Request["HTTP_X_MICROSOFTAJAX"] == null)
            {
                if (IsEncodingAccepted(DEFLATE))
                {
                    app.Response.Filter = new DeflateStream(app.Response.Filter, CompressionMode.Compress);
                    SetEncoding(DEFLATE);
                }
                else if (IsEncodingAccepted(GZIP))
                {
                    app.Response.Filter = new GZipStream(app.Response.Filter, CompressionMode.Compress);
                    SetEncoding(GZIP);
                }
            }
        }
    }

    /// <summary>
    /// Checks the request headers to see if the specified
    /// encoding is accepted by the client.
    /// </summary>
    private static bool IsEncodingAccepted(string encoding)
    {
        HttpContext context = HttpContext.Current;
        return context.Request.Headers["Accept-encoding"] != null && context.Request.Headers["Accept-encoding"].Contains(encoding);
    }

    /// <summary>
    /// Adds the specified encoding to the response headers.
    /// </summary>
    /// <param name="encoding"></param>
    private static void SetEncoding(string encoding)
    {
        HttpContext.Current.Response.AppendHeader("Content-encoding", encoding);
    }

    #endregion

    #region Compress WebResource.axd

    /// <summary>
    /// Handles the BeginRequest event of the context control.
    /// </summary>
    /// <param name="sender">The source of the event.</param>
    /// <param name="e">The <see cref="System.EventArgs"/> instance containing the event data.</param>
    private void context_BeginRequest(object sender, EventArgs e)
    {
        HttpApplication app = (HttpApplication)sender;
        if (app.Request.Path.Contains("WebResource.axd"))
        {
            SetCachingHeaders(app);

            if (IsBrowserSupported() && app.Context.Request.QueryString["c"] == null && (IsEncodingAccepted(DEFLATE) || IsEncodingAccepted(GZIP)))
                app.CompleteRequest();
        }
    }

    /// <summary>
    /// Handles the EndRequest event of the context control.
    /// </summary>
    /// <param name="sender">The source of the event.</param>
    /// <param name="e">The <see cref="System.EventArgs"/> instance containing the event data.</param>
    private void context_EndRequest(object sender, EventArgs e)
    {
        if (!IsBrowserSupported() || (!IsEncodingAccepted(DEFLATE) && !IsEncodingAccepted(GZIP)))
            return;

        HttpApplication app = (HttpApplication)sender;
        string key = app.Request.QueryString.ToString();

        if (app.Request.Path.Contains("WebResource.axd") && app.Context.Request.QueryString["c"] == null)
        {
            if (app.Application[key] == null)
            {
                AddCompressedBytesToCache(app, key);
            }

            SetEncoding((string)app.Application[key + "enc"]);
            app.Context.Response.ContentType = "text/javascript";
            app.Context.Response.BinaryWrite((byte[])app.Application[key]);
        }
    }

    /// <summary>
    /// Sets the caching headers and monitors the If-None-Match request header,
    /// to save bandwidth and CPU time.
    /// </summary>
    private static void SetCachingHeaders(HttpApplication app)
    {
        string etag = "\"" + app.Context.Request.QueryString.ToString().GetHashCode().ToString() + "\"";
        string incomingEtag = app.Request.Headers["If-None-Match"];

        app.Response.Cache.VaryByHeaders["Accept-Encoding"] = true;
        app.Response.Cache.SetExpires(DateTime.Now.AddDays(30));
        app.Response.Cache.SetCacheability(HttpCacheability.Public);
        app.Response.Cache.SetLastModified(DateTime.Now.AddDays(-30));
        app.Response.Cache.SetETag(etag);

        if (String.Compare(incomingEtag, etag) == 0)
        {
            app.Response.StatusCode = (int)HttpStatusCode.NotModified;
            app.Response.End();
        }
    }

    /// <summary>
    /// Check if the browser is Internet Explorer 6 that have a known bug with compression
    /// </summary>
    /// <returns></returns>
    private static bool IsBrowserSupported()
    {
        // Because of bug in Internet Explorer 6
        HttpContext context = HttpContext.Current;
        return !(context.Request.UserAgent != null && context.Request.UserAgent.Contains("MSIE 6"));
    }

    /// <summary>
    /// Adds a compressed byte array into the application items.
    /// <remarks>
    /// This is done for performance reasons so it doesn't have to
    /// create an HTTP request every time it serves the WebResource.axd.
    /// </remarks>
    /// </summary>
    private static void AddCompressedBytesToCache(HttpApplication app, string key)
    {
        HttpWebRequest request = (HttpWebRequest)WebRequest.Create(app.Context.Request.Url.OriginalString + "&c=1");
        using (HttpWebResponse response = request.GetResponse() as HttpWebResponse)
        {
            Stream responseStream = response.GetResponseStream();

            using (MemoryStream ms = CompressResponse(responseStream, app, key))
            {
                app.Application.Add(key, ms.ToArray());
            }
        }
    }

    /// <summary>
    /// Compresses the response stream if the browser allows it.
    /// </summary>
    private static MemoryStream CompressResponse(Stream responseStream, HttpApplication app, string key)
    {
        MemoryStream dataStream = new MemoryStream();
        StreamCopy(responseStream, dataStream);
        responseStream.Dispose();

        byte[] buffer = dataStream.ToArray();
        dataStream.Dispose();

        MemoryStream ms = new MemoryStream();
        Stream compress = null;

        if (IsEncodingAccepted(DEFLATE))
        {
            compress = new DeflateStream(ms, CompressionMode.Compress);
            app.Application.Add(key + "enc", DEFLATE);
        }
        else if (IsEncodingAccepted(GZIP))
        {
            compress = new GZipStream(ms, CompressionMode.Compress);
            app.Application.Add(key + "enc", DEFLATE);
        }

        compress.Write(buffer, 0, buffer.Length);
        compress.Dispose();
        return ms;
    }

    /// <summary>
    /// Copies one stream into another.
    /// </summary>
    private static void StreamCopy(Stream input, Stream output)
    {
        byte[] buffer = new byte[2048];
        int read;
        do
        {
            read = input.Read(buffer, 0, buffer.Length);
            output.Write(buffer, 0, read);
        } while (read > 0);
    }

    #endregion


}

public class PreApplicationStartCode
{
    public static void Start()
    {
        // Register our module
        Microsoft.Web.Infrastructure.DynamicModuleHelper.DynamicModuleUtility.RegisterModule(typeof(GZipUmbraco));
    }
}




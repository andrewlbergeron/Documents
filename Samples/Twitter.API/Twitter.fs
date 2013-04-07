﻿namespace FSharp.TwitterAPI

open System
open System.Globalization
open System.Threading
open System.Web
open System.IO
open System.Net
open System.Security.Cryptography
open System.Text

// ----------------------------------------------------------------------------------------------

module Utils = 
  let requestTokenURI = "https://api.twitter.com/oauth/request_token"
  let accessTokenURI = "https://api.twitter.com/oauth/access_token"
  let authorizeURI = "https://api.twitter.com/oauth/authorize"

  // Utilities
  let unreservedChars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_.~";
  let urlEncode str = 
      String.init (String.length str) (fun i -> 
          let symbol = str.[i]
          if unreservedChars.IndexOf(symbol) = -1 then
              "%" + String.Format("{0:X2}", int symbol)
          else
              string symbol)


  // Core Algorithms
  let hmacsha1 signingKey str = 
      let converter = new HMACSHA1(Encoding.ASCII.GetBytes(signingKey : string))
      let inBytes = Encoding.ASCII.GetBytes(str : string)
      let outBytes = converter.ComputeHash(inBytes)
      Convert.ToBase64String(outBytes)

  let compositeSigningKey consumerSecret tokenSecret = 
      urlEncode(consumerSecret) + "&" + urlEncode(tokenSecret)

  let baseString httpMethod baseUri queryParameters = 
      httpMethod + "&" + 
      urlEncode(baseUri) + "&" +
      (queryParameters 
       |> Seq.sortBy (fun (k,v) -> k)
       |> Seq.map (fun (k,v) -> urlEncode(k)+"%3D"+urlEncode(v))
       |> String.concat "%26") 

  let createAuthorizeHeader queryParameters = 
      let headerValue = 
          "OAuth " + 
          (queryParameters
           |> Seq.map (fun (k,v) -> urlEncode(k)+"\x3D\""+urlEncode(v)+"\"")
           |> String.concat ",")
      headerValue

  let currentUnixTime() = floor (DateTime.UtcNow - DateTime(1970, 1, 1, 0, 0, 0, 0)).TotalSeconds


  /// Request a token from Twitter and return:
  ///  oauth_token, oauth_token_secret, oauth_callback_confirmed
  let public requestToken consumerKey consumerSecret = 
      let signingKey = compositeSigningKey consumerSecret ""

      let queryParameters = 
          ["oauth_callback", "oob";
           "oauth_consumer_key", consumerKey;
           "oauth_nonce", System.Guid.NewGuid().ToString().Substring(24);
           "oauth_signature_method", "HMAC-SHA1";
           "oauth_timestamp", currentUnixTime().ToString();
           "oauth_version", "1.0"]

      let signingString = baseString "POST" requestTokenURI queryParameters
      let oauth_signature = hmacsha1 signingKey signingString

      let realQueryParameters = ("oauth_signature", oauth_signature)::queryParameters

      let req = WebRequest.Create(requestTokenURI, Method="POST")
      let headerValue = createAuthorizeHeader realQueryParameters
      req.Headers.Add(HttpRequestHeader.Authorization, headerValue)
    
      let resp = req.GetResponse()
      let stream = resp.GetResponseStream()
      let txt = (new StreamReader(stream)).ReadToEnd()
    
      let parts = txt.Split('&')
      (parts.[0].Split('=').[1],
       parts.[1].Split('=').[1],
       parts.[2].Split('=').[1] = "true")

  /// Get an access token from Twitter and returns: oauth_token, oauth_token_secret
  let public accessToken consumerKey consumerSecret token tokenSecret verifier =
      let signingKey = compositeSigningKey consumerSecret tokenSecret

      let queryParameters = 
          ["oauth_consumer_key", consumerKey;
           "oauth_nonce", System.Guid.NewGuid().ToString().Substring(24);
           "oauth_signature_method", "HMAC-SHA1";
           "oauth_token", token;
           "oauth_timestamp", currentUnixTime().ToString();
           "oauth_verifier", verifier;
           "oauth_version", "1.0"]

      let signingString = baseString "POST" accessTokenURI queryParameters
      let oauth_signature = hmacsha1 signingKey signingString
    
      let realQueryParameters = ("oauth_signature", oauth_signature)::queryParameters
    
      let req = WebRequest.Create(accessTokenURI, Method="POST")
      let headerValue = createAuthorizeHeader realQueryParameters
      req.Headers.Add(HttpRequestHeader.Authorization, headerValue)
    
      let resp = req.GetResponse()
      let stream = resp.GetResponseStream()
      let txt = (new StreamReader(stream)).ReadToEnd()
    
      let parts = txt.Split('&')
      (parts.[0].Split('=').[1],
       parts.[1].Split('=').[1])

  /// Compute the 'Authorization' header for the given request data
  let authHeaderAfterAuthenticated consumerKey consumerSecret url httpMethod token tokenSecret queryParams = 
      let signingKey = compositeSigningKey consumerSecret tokenSecret

      let queryParameters = 
              ["oauth_consumer_key", consumerKey;
               "oauth_nonce", System.Guid.NewGuid().ToString().Substring(24);
               "oauth_signature_method", "HMAC-SHA1";
               "oauth_token", token;
               "oauth_timestamp", currentUnixTime().ToString();
               "oauth_version", "1.0"]

      let signingQueryParameters = 
          List.append queryParameters queryParams

      let signingString = baseString httpMethod url signingQueryParameters
      let oauth_signature = hmacsha1 signingKey signingString
      let realQueryParameters = ("oauth_signature", oauth_signature)::queryParameters
      let headerValue = createAuthorizeHeader realQueryParameters
      headerValue

  /// Add an Authorization header to an existing WebRequest 
  let addAuthHeaderForUser (webRequest : WebRequest) consumerKey consumerSecret token tokenSecret queryParams = 
      let url = webRequest.RequestUri.ToString()
      let httpMethod = webRequest.Method
      let header = authHeaderAfterAuthenticated consumerKey consumerSecret url httpMethod token tokenSecret queryParams
      webRequest.Headers.Add(HttpRequestHeader.Authorization, header)

// ----------------------------------------------------------------------------------------------

type TwitterContext(consumerKey, consumerSecret, accessToken, accessSecret) =
  member x.ConsumerKey : string = consumerKey
  member x.ConsumerSecret : string = consumerSecret
  member x.AccessToken : string = accessToken
  member x.AccessSecret : string = accessSecret

type TwitterConnector =
  abstract Connect : string -> TwitterContext

[<AutoOpen>]
module WebRequestExtensions =
  type System.Net.WebRequest with
    /// Add an Authorization header to the WebRequest for the provided user authorization tokens and query parameters
    member this.AddOAuthHeader(consumerKey, consumerSecret, userToken, userTokenSecret, queryParams) =
      Utils.addAuthHeaderForUser this consumerKey consumerSecret userToken userTokenSecret queryParams

    /// Add an Authorization header to the WebRequest for the provided user authorization tokens and query parameters
    member this.AddOAuthHeader(ctx:TwitterContext, queryParams) =
      Utils.addAuthHeaderForUser this ctx.ConsumerKey ctx.ConsumerSecret ctx.AccessToken ctx.AccessSecret queryParams


// ----------------------------------------------------------------------------------------------

type TwitterStream<'T> = 
  abstract TweetReceived : IEvent<'T>
  abstract Stop : unit -> unit
  abstract Start : unit -> unit

module Twitter =
  
  type TwitterHome = FSharp.Data.JsonProvider<"references\\home.json">
  type TwitterTweet = FSharp.Data.JsonProvider<"references\\stream.json", SampleList=true>

  let authenticate (consumer_key, consumer_secret) navigate =
    let request_token, request_secret, _ = Utils.requestToken consumer_key consumer_secret
    let url = Utils.authorizeURI + "?oauth_token=" + request_token
    navigate(url)
    { new TwitterConnector with 
        member x.Connect(number) =
          let access_token, access_secret = 
            Utils.accessToken consumer_key consumer_secret request_token request_secret number
          TwitterContext(consumer_key, consumer_secret, access_token, access_secret) }


  let requestRawData (ctx:TwitterContext) (url:string) query =
    let queryString = [for k, v in query -> k + "=" + v] |> String.concat "&"
    let req = WebRequest.Create(url + (if query <> [] then "?" + queryString else ""), Method="GET")
    req.AddOAuthHeader(ctx, query)

    use resp = req.GetResponse()
    use strm = resp.GetResponseStream()
    use sr = new StreamReader(strm)
    sr.ReadToEnd()
              
  let homeTimeline (ctx:TwitterContext) =
    let url = "https://api.twitter.com/1.1/statuses/home_timeline.json"
    let req = WebRequest.Create(url) 
    req.AddOAuthHeader(ctx, [])
    use resp = req.GetResponse()
    use strm = resp.GetResponseStream()
    use sr = new StreamReader(strm)
    TwitterHome.Parse(sr.ReadToEnd())

  let private downloadTweets (req:WebRequest) =
    let cts = new CancellationTokenSource()
    let event = new Event<_>()
    let downloadLoop = async {
      System.Net.ServicePointManager.Expect100Continue <- false
      let! resp = req.AsyncGetResponse()
      (* | :? WebException as ex ->
            let x = ex.Response :?> HttpWebResponse
            if x.StatusCode = HttpStatusCode.Unauthorized then
                // TODO need inform user login has failed and they need to try again
                printfn "Here?? %O" ex
            reraise() *)
      use stream = resp.GetResponseStream()
      use reader = new StreamReader(stream)
    
      while not reader.EndOfStream do
        let sizeLine = reader.ReadLine()
        if not (String.IsNullOrEmpty sizeLine) then 
          let size = int sizeLine
          let buffer = Array.zeroCreate size
          let _numRead = reader.ReadBlock(buffer,0,size) 
          let text = new System.String(buffer)
          event.Trigger(TwitterTweet.Parse(text)) }
    
    { new TwitterStream<_> with
        member x.Start() = Async.Start(downloadLoop , cts.Token)
        member x.Stop() = cts.Cancel() 
        member x.TweetReceived = event.Publish }

  let sampleTweets (ctx:TwitterContext) = 
    let req = WebRequest.Create("https://stream.twitter.com/1.1/statuses/sample.json", Method="POST", ContentType = "application/x-www-form-urlencoded")
    req.AddOAuthHeader(ctx, ["delimited", "length"])
    req.Timeout <- 10000
    do use reqStream = req.GetRequestStream() 
       use streamWriter = new StreamWriter(reqStream)
       streamWriter.Write(sprintf "delimited=length")

    downloadTweets req

  let searchTweets (ctx:TwitterContext) keywords =
    let query = String.concat "," keywords
    let req = WebRequest.Create("https://stream.twitter.com/1.1/statuses/filter.json", Method="POST", ContentType = "application/x-www-form-urlencoded")
    req.AddOAuthHeader(ctx, ["delimited", "length"; "track", query])
    req.Timeout <- 10000
    do use reqStream = req.GetRequestStream() 
       use streamWriter = new StreamWriter(reqStream)
       streamWriter.Write(sprintf "delimited=length&track=%s" query)

    downloadTweets req
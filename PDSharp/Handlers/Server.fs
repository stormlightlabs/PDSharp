namespace PDSharp.Handlers

open Microsoft.AspNetCore.Http
open Giraffe
open PDSharp.Core.Config

// =========================================
// Server & Meta Handlers
// =========================================

module Server =
  [<CLIMutable>]
  type DescribeServerResponse = {
    availableUserDomains : string list
    did : string
    inviteCodeRequired : bool
  }

  let describeServerHandler : HttpHandler =
    fun next ctx ->
      let config = ctx.GetService<AppConfig>()

      let response = {
        availableUserDomains = []
        did = config.DidHost
        inviteCodeRequired = true
      }

      json response next ctx

  let indexHandler : HttpHandler =
    fun next ctx ->
      let html =
        """<html>
  <head><title>PDSharp</title></head>
  <body>
    <pre>
        888                             888                          8888888888   888  888
        888                             888                          888          888  888
        888                             888                          888        888888888888
8888b.  888888 88888b.  888d888 .d88b.  888888 .d88b.        88      8888888      888  888
    "88b 888    888 "88b 888P"  d88""88b 888   d88""88b    888888    888          888  888
.d888888 888    888  888 888    888  888 888   888  888      88      888        888888888888
888  888 Y88b.  888 d88P 888    Y88..88P Y88b. Y88..88P              888          888  888
"Y888888  "Y888 88888P"  888     "Y88P"   "Y888 "Y88P"               888          888  888
                888
                888
                888


This is an AT Protocol Personal Data Server (aka, an atproto PDS)

Most API routes are under /xrpc/

         Code: https://github.com/bluesky-social/atproto
               https://github.com/stormlightlabs/PDSharp
               https://tangled.org/desertthunder.dev/PDSharp
    Self-Host: https://github.com/bluesky-social/pds
    Protocol:  https://atproto.com
    </pre>
  </body>
</html>"""

      ctx.SetContentType "text/html"
      ctx.SetStatusCode 200
      ctx.WriteStringAsync html

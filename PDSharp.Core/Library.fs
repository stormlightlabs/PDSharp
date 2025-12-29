namespace PDSharp.Core

module Models =
  [<CLIMutable>]
  type DescribeServerResponse = {
    availableUserDomains : string list
    did : string
    inviteCodeRequired : bool
  }

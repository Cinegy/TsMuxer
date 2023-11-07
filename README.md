# Cinegy.TsMuxer

## What is it?

TSMuxer is a simple tool, designed to help make testing easier for people with two streams they need to merge - for example, a main playout stream and a smaller subtitling stream.

It's not 'production ready' - it's really designed as a stand-in for a full quality muxer that would be used at a broadcast site for light duties in a lab or during demos or training. 

However, it works - although at the moment it is young and has not spent long being tuned or having any features added to allow fine-grained controls.

## How easy is it?

Well, we've added everything you need into a single teeny-tiny EXE again, which just depends on .NET 4.5. And then we gave it all a nice Apache license, so you can tinker and throw the tool wherever you need to on the planet.

Just run the EXE from inside a command-prompt, and the application will just run - although you might want to provide it with some arguments too.

## Command line arguments:

Run with a --help argument, and you will get interactive help information like this:

```
C:\Program Files\Cinegy>Cinegy.TsMuxer.exe
Cinegy 0.0.0.1
Copyright cCinegy GmbH 2017

ERROR(S):
  Required option 'm, mainmulticastaddress' is missing.
  Required option 'p, mainmulticastport' is missing.
  Required option 'n, submulticastaddress' is missing.
  Required option 'q, submulticastport' is missing.
  Required option 'o, outputmulticastaddress' is missing.
  Required option 'r, outputmulticastport' is missing.
  Required option 's, subpids' is missing.

  -a, --multicastadapter          IP address of the adapter to listen for multicast data (if not set, tries first
                                  binding adapter).

  -m, --mainmulticastaddress      Required. Primary multicast address to subscribe this instance to.

  -p, --mainmulticastport         Required. Primary multicast port number to subscribe this instance to.

  -n, --submulticastaddress       Required. Primary multicast address to subscribe this instance to.

  -q, --submulticastport          Required. Primary multicast port number to subscribe this instance to.

  -o, --outputmulticastaddress    Required. Output multicast address to send results to.

  -r, --outputmulticastport       Required. Output multicast port number to send results to.

  -h, --nortpheaders              (Default: false) Optional instruction to skip the expected 12 byte RTP headers
                                  (meaning plain MPEGTS inside UDP is expected)

  -s, --subpids                   Required. Comma separated list of sub stream PIDs to map into master

  -l, --logfile                   Optional file to record events to.

  -d, --descriptortags            (Default: ) Comma separated tag values added to all log entries for instance and
                                  machine identification

  -t, --telemetry                 Enable integrated telemetry

  --help                          Display this help screen.

  --version                       Display version information.

Hit enter to quit
```

Just to make your life easier, we auto-build this using AppVeyor - here is how we are doing right now: 

[![Build status](https://ci.appveyor.com/api/projects/status/njky44r567b8x634?svg=true)](https://ci.appveyor.com/project/cinegy/tsmuxer)

You can check out the latest compiled binary from the master or pre-master code here:

[Download TSMuxer Binary Artifacts](https://ci.appveyor.com/project/cinegy/tsmuxer/build/artifacts)

# Cinegy.TsMuxer


## How easy is it?

Well, we've added everything you need into a single teeny-tiny EXE again, which just depends on .NET 4.5. And then we gave it all a nice Apache license, so you can tinker and throw the tool wherever you need to on the planet.

Just run the EXE from inside a command-prompt, and the application will just run - although you might want to provide it with some arguments.

## Command line arguments:

Run with a --help argument, and you will get interactive help information like this:

```
Cinegy.TsMuxer 0.0.13.0
Copyright cCinegy GmbH 2017

ERROR(S):
  Required option 'm, multicastaddress' is missing.
  Required option 'g, multicastgroup' is missing.
  Required option 'u, urlidentifier' is missing.

  -a, --adapter              IP address of the adapter to serve HTTP requests from (if not set, tries first binding adapter).
  -b, --multicastadapter     IP address of the adapter to listen for multicast data (if not set, tries first binding adapter).
  -m, --multicastaddress     Required. Multicast address to subscribe this instance to.
  -g, --multicastgroup       Required. Multicast group (port number) to subscribe this instance to.
  -n, --nortpheaders         (Default: false) Optional instruction to skip the expected 12 byte RTP headers (meaning
                             plain MPEGTS inside UDP is expected
  -s, --buffersize           (Default: 100000) Optional instruction to control the number of TS packets cached within
                             random access window buffer
  -u, --urlidentifier        Required. Text identifier to append to base URL for identifying this specific stream.
  -q, --quiet                (Default: false) Don't print anything to the console
  -l, --logfile              Optional file to record events to.
  -d, --descriptortags       (Default: ) Comma separated tag values added to all log entries for instance and machine
                             identification
  -e, --timeserieslogging    Record time slice metric data to.
  -v, --verboselogging       Creates event logs for all discontinuities and skips.
  -p, --port                 (Default: 8082) Port Number to listen for web serving requests (8082 if not set).

  --help                     Display this help screen.
  --version                  Display version information.

Hit enter to quit
```

Just to make your life easier, we auto-build this using AppVeyor - here is how we are doing right now: 

[![Build status](https://ci.appveyor.com/api/projects/status/sm2dhprb2sj27j0u?svg=true)](https://ci.appveyor.com/project/cinegy/Cinegy.TsMuxer/branch/master)

You can check out the latest compiled binary from the master or pre-master code here:

[AppVeyor RtpHttpGateway Project Builder](https://ci.appveyor.com/project/cinegy/cinegy.TsMuxer/build/artifacts)

# Manual verification against OBS

The automated suite proves frame transport and discovery end to end on CI (byte-exact, including
across separate processes). The one thing it cannot cover is interop with a real third-party app.
This is a quick manual check on a Mac with OBS installed.

## Publish to OBS (we send, OBS receives)

1. Build the native shim and run the sample peer as a server:
   ```sh
   bash native/build-native.sh
   dotnet run --project samples/Syphon.NET.Peer -- server --name "Syphon.NET Demo" --seconds 120
   ```
2. In OBS, add a source: **Syphon Client** (install the obs-syphon plugin if it is not listed).
3. Choose **Syphon.NET Demo**. You should see the test pattern (a per-pixel gradient).

## Receive from another app (we receive)

1. Start any Syphon server - for example OBS's **Syphon Output** (Tools menu), or another app
   that publishes Syphon (Resolume, a VJ app, `syphon-python`).
2. List and connect from the sample peer's `crosstest`/`probe` building blocks, or in your own
   code:
   ```csharp
   using var directory = new SyphonServerDirectory();
   directory.PumpEvents(TimeSpan.FromSeconds(1)); // console host: pump the run loop
   var servers = directory.GetServers();          // expect the external server listed
   using var client = directory.CreateClient(0);
   using var frame = client.TryGetFrame();        // a real frame from the other app
   ```
3. Confirm a frame arrives and its dimensions match the source.

> In a non-GUI host, pump the run loop (`PumpEvents`) so discovery works; an app with a Cocoa run
> loop does not need to.

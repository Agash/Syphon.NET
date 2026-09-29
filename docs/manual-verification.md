# Manual verification against OBS

The automated suite proves frame transport and discovery byte-exact against the Syphon framework in a
separate process. What it cannot cover is a real third-party application. This is a quick check on a Mac
with OBS and its Syphon plugin installed.

## OBS receives

1. Run a server that publishes a moving pattern, for example:
   ```csharp
   using SyphonServer server = new("Syphon.NET Demo");
   byte[] pixels = new byte[640 * 360 * 4];
   for (int frame = 0; ; frame++)
   {
       for (int i = 0; i < pixels.Length; i += 4)
       {
           pixels[i] = (byte)(i / 4 % 640 + frame);   // blue: moving horizontal gradient
           pixels[i + 1] = (byte)(i / 4 / 640);       // green: vertical gradient
           pixels[i + 3] = 255;
       }

       server.PublishPixels(pixels, 640, 360);
       await Task.Delay(16);
   }
   ```
2. In OBS, add a **Syphon Client** source and choose **Syphon.NET Demo**. The gradient should scroll.

## OBS publishes

1. In OBS, start **Syphon Output** (Tools menu).
2. Receive it:
   ```csharp
   using SyphonServerDirectory directory = new();
   SyphonServerDescription obs = await directory.WaitForServerAsync(s => s.AppName.Contains("OBS"));
   using SyphonClient client = new(obs);
   await client.RunAsync((in SyphonFrame frame) => Console.WriteLine($"{frame.Width}x{frame.Height}"));
   ```
3. Frames should arrive at OBS's frame rate with OBS's output size.

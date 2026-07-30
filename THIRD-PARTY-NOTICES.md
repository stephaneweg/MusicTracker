# Third-party notices

MusicTracker incorporates the following third-party components. Each is used under the terms
of its own licence, reproduced below.

---

## MeltySynth

A SoundFont synthesizer, by Nobuaki Tanaka — https://github.com/sinshu/meltysynth
The source is vendored under `MeltySynth/` (see also `MeltySynth/LICENSE.txt`).

```
MIT License

Copyright (c) 2021 Nobuaki Tanaka

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

---

## NAudio

Audio input/output and MP3 encoding, by Mark Heath and contributors —
https://github.com/naudio/NAudio (used via the NuGet package).

```
MIT License

Copyright (c) Mark Heath and contributors

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

---

## VST.NET (obiwanjacobi/vst.net, VST.NET2-Host distribution)

VST2 plugin hosting is provided by VST.NET 2.x, by Marc Jacobi —
https://github.com/obiwanjacobi/vst.net (distributed as the `VST.NET2-Host` NuGet
package, version 2.1.10 or newer). The runtime DLLs `Jacobi.Vst.Core.dll` and
`Jacobi.Vst.Host.Interop.dll` — plus the .NET C++/CLI activator `Ijwhost.dll` —
ship alongside the executable and are used dynamically at runtime.

VST.NET is licensed under the GNU Lesser General Public License version 2.1
(LGPL-2.1-only). The full licence text is available at
https://www.gnu.org/licenses/old-licenses/lgpl-2.1.html. Koton Studio uses VST.NET
via dynamic linking; users are free to substitute the shipped DLLs with a rebuilt
version of the VST.NET libraries obtained from the upstream repository above.

VST is a trademark of Steinberg Media Technologies GmbH. Koton Studio hosts VST2
plugins strictly as a host — no VST SDK code is distributed with the application.

---

## Steinberg VST3 SDK (interface definitions only)

VST3 plugin hosting is implemented by direct P/Invoke against the Windows binary
interface described by the Steinberg VST3 SDK — https://github.com/steinbergmedia/vst3sdk.
The Koton Studio source files under `MusicTracker/Engine/Timeline/Vst3/Interop/`
re-express in C# the COM interface signatures, TUID constants and struct layouts
needed to call `IPluginFactory`, `IComponent`, `IAudioProcessor`, `IEditController`,
`IPlugView`, `IEventList`, `IParameterChanges` and `IBStream`.

The SDK itself is distributed under the GNU General Public License version 3
(GPL-3.0-only), see https://www.gnu.org/licenses/gpl-3.0.html. No SDK source code
is bundled with Koton Studio — only the derived interface bindings needed to talk
to third-party VST3 plugins. VST3 is a trademark of Steinberg Media Technologies GmbH;
Koton Studio acts strictly as a host.

---

The default SoundFont offered for download at first launch, **MuseScore_General.sf2**, is
distributed by MuseScore and is not bundled with this application.

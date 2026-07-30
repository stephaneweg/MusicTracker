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

## VST.NET (TruDan-VST.NET distribution)

VST2 plugin hosting is provided by VST.NET, by Marc Jacobi and TruDan —
https://github.com/obiwanjacobi/vst.net (distributed as the `TruDan-VST.NET` NuGet
package). The runtime DLLs `Jacobi.Vst.Core.dll` and `Jacobi.Vst.Interop.dll` ship
alongside the executable and are used dynamically at runtime.

VST.NET is licensed under the GNU Lesser General Public License version 2.1 (LGPL-2.1).
The full licence text is available at https://www.gnu.org/licenses/old-licenses/lgpl-2.1.html
and reproduced in `LICENSE-VST.NET.txt` next to this file. Koton Studio uses VST.NET
via dynamic linking; users are free to substitute the shipped DLLs with a rebuilt
version of the VST.NET libraries obtained from the upstream repository above.

VST is a trademark of Steinberg Media Technologies GmbH. Koton Studio hosts VST2
plugins strictly as a host — no VST SDK code is distributed with the application.

---

The default SoundFont offered for download at first launch, **MuseScore_General.sf2**, is
distributed by MuseScore and is not bundled with this application.

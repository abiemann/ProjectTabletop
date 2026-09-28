# Third-party notices

These notices cover the open-source code used for Paint's fluid simulation. Other
dependencies retain their own package and model license notices.

## Fluid Paint by David Li

Source: [dli/paint](https://github.com/dli/paint), commit
[`6b8adfa226ecda14546c90b14a3d2a0d4898ad89`](https://github.com/dli/paint/tree/6b8adfa226ecda14546c90b14a3d2a0d4898ad89).

ProjectTabletop adapts the GPU fluid passes and paint-surface shading from
`simulator.js` and the upstream `shaders` directory into its C#/Direct2D renderer.
The brush interface is replaced with disturbance-triggered drops. The original
WebGL application is not bundled or executed.

```text
The MIT License (MIT)

Copyright (c) 2017 David Li (http://david.li)

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

## ComputeSharp

The Paint GPU shaders use [ComputeSharp](https://github.com/Sergio0694/ComputeSharp)
3.2.0 through `ComputeSharp.D2D1.WinUI` and its `ComputeSharp.D2D1` dependency.
Their upstream revision is
[`9a7c9e0c755bf68447f7293e5729547750fe6be3`](https://github.com/Sergio0694/ComputeSharp/tree/9a7c9e0c755bf68447f7293e5729547750fe6be3).

```text
MIT License

Copyright (c) 2024 Sergio Pedri

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

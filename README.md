# MathBoard

[Русский](README.ru.md) | **English**

<a href="https://github.com/VortexSo4/MathBaord/releases/latest"><img alt="Download" src="https://img.shields.io/github/v/release/VortexSo4/MathBaord?style=for-the-badge&label=Download&color=2ea043&logo=data%3Aimage%2Fsvg%2Bxml%3Bbase64%2CPHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciIHZpZXdCb3g9IjAgMCAyNCAyNCI%2BPHBhdGggZmlsbD0iI2ZmZiIgZD0iTTEwLjUgMmgzdjEwLjRsMy42LTMuNiAyLjEgMi4xTDEyIDE4LjEgNC44IDEwLjlsMi4xLTIuMSAzLjYgMy42ek0zIDE5LjVoMThWMjJIM3oiLz48L3N2Zz4%3D"></a>
<img alt="Windows 10 | 11, 64-bit" src="https://img.shields.io/badge/Windows%2010%20%7C%2011-64--bit-0078D6?style=for-the-badge&logo=data%3Aimage%2Fsvg%2Bxml%3Bbase64%2CPHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciIHZpZXdCb3g9IjAgMCAyNCAyNCI%2BPHBhdGggZmlsbD0iI2ZmZiIgZD0iTTEgMWgxMC41djEwLjVIMXpNMTIuNSAxSDIzdjEwLjVIMTIuNXpNMSAxMi41aDEwLjVWMjNIMXpNMTIuNSAxMi41SDIzVjIzSDEyLjV6Ii8%2BPC9zdmc%2B">

A whiteboard for math lessons. It is made for interactive whiteboards and ordinary computers, for writing with a pen, a finger or a mouse. The board is infinite: you move and zoom it with two fingers, or with a mouse, where the wheel scrolls and Ctrl+wheel zooms.

Besides freehand drawing, it has what is usually needed for math. Drawing a line, a triangle, a rectangle or an oval and holding the pen still, turns the sketch into a perfect shape that you can resize right away without lifting the pen. Function graphs can be built from a formula. There is also a grid and a coordinate plane, 2d and 3d shapes, filling and hatching of areas, and pictures and screenshots inserting.

Lessons are plain files in a folder you choose, listed in the panel on the left. Changes are saved automatically, and the app has auto updates. A finished board can be exported to PNG or PDF.

Tool buttons sit along the edges of the screen, which you can move around or remove. Holding the pen still opens a radial menu with the brush, eraser, line width and other actions; its content can be changed too. You can erase with your palm, like with a sponge.

## Building

```
dotnet build MathBoard.sln
dotnet run --project MathBoard
```
Requires .NET 9 SDK.

License: [GNU LGPL v3](LICENSE).

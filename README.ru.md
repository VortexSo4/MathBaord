# MathBoard

**Русский** | [English](README.md)

<a href="https://github.com/VortexSo4/MathBaord/releases/latest"><img alt="Скачать" src="https://img.shields.io/github/v/release/VortexSo4/MathBaord?style=for-the-badge&label=%D0%A1%D0%BA%D0%B0%D1%87%D0%B0%D1%82%D1%8C&color=2ea043&logo=data%3Aimage%2Fsvg%2Bxml%3Bbase64%2CPHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciIHZpZXdCb3g9IjAgMCAyNCAyNCI%2BPHBhdGggZmlsbD0iI2ZmZiIgZD0iTTEwLjUgMmgzdjEwLjRsMy42LTMuNiAyLjEgMi4xTDEyIDE4LjEgNC44IDEwLjlsMi4xLTIuMSAzLjYgMy42ek0zIDE5LjVoMThWMjJIM3oiLz48L3N2Zz4%3D"></a>
<img alt="Windows 10 | 11, 64-bit" src="https://img.shields.io/badge/Windows%2010%20%7C%2011-64--bit-0078D6?style=for-the-badge&logo=data%3Aimage%2Fsvg%2Bxml%3Bbase64%2CPHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciIHZpZXdCb3g9IjAgMCAyNCAyNCI%2BPHBhdGggZmlsbD0iI2ZmZiIgZD0iTTEgMWgxMC41djEwLjVIMXpNMTIuNSAxSDIzdjEwLjVIMTIuNXpNMSAxMi41aDEwLjVWMjNIMXpNMTIuNSAxMi41SDIzVjIzSDEyLjV6Ii8%2BPC9zdmc%2B">

Доска для уроков математики. Создана для работы на интерактивной доске или обычном компьютере для написания пером, пальцем или мышью. Поле бесконечное - руками движение и масштабирование работает двумя пальцами, мышью движение колёсиком и масштаб колесиком с Ctrl.

Помимо рисования от руки, тут есть все что обычно нужно для математики. Нарисовав отрезок, треугольник, прямоугольник или овал и задержав перо на месте, набросок станет ровной фигурой, и её размеры можно сразу редактировать, не отрывая пера. График функции строится по формуле. Также есть сетка и координатная плоскость, объёмные и плоские фигуры, заливка и штриховка областей, вставка картинок и снимков экрана.

Уроки лежат обычными файлами в папке, которую вы выбираете, и видны в панели слева. Изменения сохраняются сами, а о новых версиях программа сообщает при запуске. Готовую доску можно выгрузить в PNG или PDF.

Кнопки инструментов расставлены по краям экрана, их можно переставлять и убирать. Подержав перо неподвижно, откроется круговое меню с кистью, ластиком, толщиной линии и другими действиями, состав также настраивается. Ладонью можно стирать, как губкой.

## Сборка

```
dotnet build MathBoard.sln
dotnet run --project MathBoard
```
Требуется .NET 9 SDK

Лицензия - [GNU LGPL v3](LICENSE).
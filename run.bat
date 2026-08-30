@echo off
dotnet clean
dotnet restore
dotnet build
dotnet run --project XCL2.App
dotnet build -c Release
echo 输出在：XCL2.App\bin\Release\net8.0-windows\
echo 按任意键退出
pause
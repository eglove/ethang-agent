Remove-Item -Recurse "C:\Users\glove\OneDrive\Desktop\eThangAgent"
dotnet publish src/eThangAgent.Desktop/eThangAgent.Desktop.csproj -c Release -o "C:\Users\glove\OneDrive\Desktop\eThangAgent"
Start-Process "C:\Users\glove\OneDrive\Desktop\eThangAgent\eThangAgent.Desktop.exe"

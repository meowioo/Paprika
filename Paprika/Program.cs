using System.Text;
using Paprika.Ui;

Console.OutputEncoding = Encoding.UTF8;
Console.InputEncoding = Encoding.UTF8;

// Paprika 现在是纯控制台交互应用，不再解析命令行参数。
// 无论双击还是从终端启动，都进入同一套菜单流程。
return await InteractiveApp.RunAsync(CancellationToken.None);


using System;
using System.IO;
using System.Linq;
using System.Text;

namespace LightNote {
 public static class SelfTest {
  public static void Run(string folder) {
   Directory.CreateDirectory(folder); string result = Path.Combine(folder, "test-results.txt");
   try {
    string testFolder = Path.Combine(folder, "case-" + Guid.NewGuid().ToString("N"));
    var store = new Store(testFolder); var state = store.Load();
    Assert(state.Notes.Count == 0, "空数据初始化");
    var note = new Note { Title = "中文与 Unicode 📝", Body = "第一行\r\n第二行\t引号\"和反斜线\\", Pinned = true, Color = 2 };
    state.Notes.Add(note); state.CurrentId = note.Id; store.Save(state);
    var roundtrip = store.Load();
    Assert(roundtrip.Notes[0].Body == note.Body && roundtrip.Notes[0].Title == note.Title && roundtrip.Notes[0].Pinned && roundtrip.CurrentId == note.Id, "中文、换行及状态保存恢复");
    note.Body = "更新版"; store.Save(state);
    Assert(Store.Read(store.FilePath).Notes[0].Body == "更新版", "原子替换");
    Assert(Store.Read(store.FilePath + ".bak").Notes[0].Body != "更新版", "保留上一版备份");
    File.WriteAllText(store.FilePath, "{损坏", Encoding.UTF8); var recovered = store.Load();
    Assert(store.Recovered && recovered.Notes[0].Body != "更新版" && Directory.GetFiles(testFolder, "*.damaged-*").Length == 1, "损坏恢复与原件保留");
    store.Save(recovered); Assert(Store.Read(store.FilePath + ".bak").Notes.Count == 1, "恢复后再次保存仍保有有效备份");
    long now = DateTime.UtcNow.Ticks; note.DueUtc = now - 1;
    Assert(note.IsDue(now), "过期提醒补报");
    note.Alerting = true; note.DueUtc = now + TimeSpan.TicksPerDay;
    Assert(note.IsDue(now), "未处理弹窗在重启后继续提醒");
    note.Snooze(new DateTime(now, DateTimeKind.Utc));
    Assert(!note.Alerting && note.DueUtc == now + 10 * TimeSpan.TicksPerMinute && !note.IsDue(now), "稍后十分钟");
    note.Complete(); Assert(!note.IsDue(now) && note.DueUtc == 0, "确认后取消提醒");
    note.DueUtc = now - 1; note.DeletedUtc = now; Assert(!note.IsDue(now), "已删除便笺不提醒");
    recovered.Notes[0].DeletedUtc = now; store.Save(recovered); Assert(store.Load().Notes[0].DeletedUtc == now, "回收站状态保存");
    File.WriteAllText(store.FilePath, "broken"); File.WriteAllText(store.FilePath + ".bak", "broken");
    bool rejected = false; try { store.Load(); } catch (InvalidDataException) { rejected = true; }
    Assert(rejected, "双文件损坏时拒绝覆盖用户数据");
    File.WriteAllText(result, "PASS — 13 checks\r\n保存、备份、损坏恢复、提醒状态和回收站验证通过。", Encoding.UTF8);
   } catch (Exception ex) { File.WriteAllText(result, "FAIL\r\n" + ex, Encoding.UTF8); Environment.ExitCode = 1; }
  }
  static void Assert(bool condition, string name) { if (!condition) throw new Exception("检查失败：" + name); }
 }
}

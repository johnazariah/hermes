#r "../tests/Hermes.Tests/bin/Debug/net9.0/Microsoft.Data.Sqlite.dll"
open Microsoft.Data.Sqlite
let conn = new SqliteConnection("Data Source=C:\\Users\\johnaz\\Documents\\Hermes\\db.sqlite")
conn.Open()
let cmd = conn.CreateCommand()
cmd.CommandText <- "DELETE FROM sync_state"
let n = cmd.ExecuteNonQuery()
printfn "Deleted %d sync_state rows — next sync will start from June 2024" n
conn.Close()

#r "c:/work/hermes/tests/Hermes.Tests/bin/Debug/net9.0/Microsoft.Data.Sqlite.dll"
open Microsoft.Data.Sqlite
let conn = new SqliteConnection("Data Source=C:\\Users\\johnaz\\Documents\\Hermes\\db.sqlite")
conn.Open()
let cmd = conn.CreateCommand()
cmd.CommandText <- "UPDATE documents SET category = 'unclassified', classification_tier = NULL, classification_confidence = NULL WHERE category NOT IN ('unclassified')"
let rows = cmd.ExecuteNonQuery()
printfn "Updated %d rows" rows

// Also update saved_path to reflect unclassified location
let cmd2 = conn.CreateCommand()
cmd2.CommandText <- "UPDATE documents SET saved_path = 'unclassified/' || original_name WHERE category = 'unclassified' AND saved_path NOT LIKE 'unclassified/%'"
let rows2 = cmd2.ExecuteNonQuery()
printfn "Updated %d saved paths" rows2

conn.Close()

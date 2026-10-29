# Benchmark: counting dirty cells in a 50,000-row CSV

Five implementations of one rule set, same input, same output, measured on the
same machine. Every number below comes from a run that actually happened; the
raw output of all runs is reproduced at the end of this file.

## What is measured

This is **not** a benchmark of the desktop application. It measures the core
work the app does — walk every cell of a CSV and classify it — as a standalone
command-line counter with a deliberately small rule set, so that five languages
can be compared without a GUI in the middle.

The app itself applies a richer rule set (missing values, date format, malformed
e-mail, non-numeric values in numeric columns, ragged rows, duplicate rows) while
the grid stays interactive. Those extra checks are not part of this benchmark.

## Input

| field | value |
|---|---|
| rows | 50,000 data rows + 1 header |
| columns | 25 (`id,name,phone,email,f5..f21,joined,amount,status,note`) |
| size | 8,725,137 bytes |
| encoding | UTF-8, CRLF, no quoted fields |
| sha256 | `80f6c4bdee55fe4d4fb578ea8f86b664aa5f80ca3e0899cbb225995079c26c3f` |

The file is synthetic and is not shipped in this repository.

## Rules (byte-identical in all five implementations)

Header = first non-empty line; rows = every non-empty line after it. A line is
split on `,` after removing a trailing `\r`. The `joined` column is located by
header name. Each cell is tested in this order and counted **at most once**:

1. **empty** — the cell has zero bytes.
2. **bad_date** — the cell is in `joined` and is not a valid `YYYY-MM-DD`
   calendar date (real month lengths, leap years honoured).
3. **mojibake** — the cell has at least one non-ASCII character, *every*
   character maps back into the CP1252 repertoire, and the reversed byte string
   contains at least one strictly valid multi-byte UTF-8 sequence at any offset.

`errors = empty + bad_date + mojibake`.

The mojibake test deliberately does not require the whole reversed string to be
valid UTF-8: real mojibake produced by decoding UTF-8 as CP1252 with undefined
bytes dropped can end mid-sequence. Text in a non-CP1252 script (CJK, Cyrillic,
…) is never a candidate — those characters have no CP1252 byte.

## Environment

| | |
|---|---|
| CPU | AMD Ryzen 5 5600X, 6 cores / 12 threads |
| RAM | 32 GB |
| OS | Windows 10 (10.0.19041) |
| .NET | 8.0.413 |
| Rust | rustc 1.98.0 |
| Go | 1.23.5 |
| Java | OpenJDK 17.0.17 |
| Python | CPython 3.14.7 |

`ms` is the wall time of the processing region only: the file is read into
memory before the timer starts, and the timer stops when the last row is
counted. Process start-up is excluded, so JVM and CPython start-up are not
charged to those languages. Builds are optimised (`-O2` / `-O` / `-c Release`).

## Results — 3 runs each, milliseconds

| Language | min | median | relative to fastest |
|---|---:|---:|---:|
| Rust | 13.3 | 13.5 | 1.00× |
| Go | 19.4 | 20.0 | 1.45× |
| C# (.NET 8) | 24.0 | 24.4 | 1.80× |
| Java 17 | 104.5 | 104.7 | 7.83× |
| Python 3.14 | 405.3 | 427.9 | 30.4× |

C and C++ were intended to be in the set, but the benchmark machine had no C or
C++ compiler installed at the time of the run, so **no C/C++ number is reported
here**. Nothing is estimated or interpolated.

## Correctness

All fifteen runs — five implementations × three repetitions — printed identical
counts:

```
rows=50000 errors=163400 empty=131514 bad_date=3847 mojibake=28039
```

A language is only in the table if it reproduced that line exactly.

## Raw runs

```
--- round 1 ---
csharp rows=50000 errors=163400 empty=131514 bad_date=3847 mojibake=28039 ms=24.373
rust   rows=50000 errors=163400 empty=131514 bad_date=3847 mojibake=28039 ms=13.521
go     rows=50000 errors=163400 empty=131514 bad_date=3847 mojibake=28039 ms=22.319
java   rows=50000 errors=163400 empty=131514 bad_date=3847 mojibake=28039 ms=104.457
python rows=50000 errors=163400 empty=131514 bad_date=3847 mojibake=28039 ms=427.874
--- round 2 ---
csharp rows=50000 errors=163400 empty=131514 bad_date=3847 mojibake=28039 ms=25.783
rust   rows=50000 errors=163400 empty=131514 bad_date=3847 mojibake=28039 ms=13.342
go     rows=50000 errors=163400 empty=131514 bad_date=3847 mojibake=28039 ms=20.022
java   rows=50000 errors=163400 empty=131514 bad_date=3847 mojibake=28039 ms=134.595
python rows=50000 errors=163400 empty=131514 bad_date=3847 mojibake=28039 ms=452.956
--- round 3 ---
csharp rows=50000 errors=163400 empty=131514 bad_date=3847 mojibake=28039 ms=23.968
rust   rows=50000 errors=163400 empty=131514 bad_date=3847 mojibake=28039 ms=13.810
go     rows=50000 errors=163400 empty=131514 bad_date=3847 mojibake=28039 ms=19.351
java   rows=50000 errors=163400 empty=131514 bad_date=3847 mojibake=28039 ms=104.745
python rows=50000 errors=163400 empty=131514 bad_date=3847 mojibake=28039 ms=405.290
```

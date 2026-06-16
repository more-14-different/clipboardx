# Storage And Search Architecture Comparison

This note compares several local-first tools and patterns that are relevant to long-term clipboard history storage and search:

- `clipboardx`
- `CopyQ`
- `qollect`
- `Orca Note`
- `SiYuan`
- Quicker-style file archive + `rg`/`ugrep`/`fzf`

The goal is not to declare a universal winner. The goal is to extract practical design patterns that are useful for `clipboardx`.

## Summary Table

| System / Pattern | Source Of Truth | Search Path | Index Type | Data Loading Style | Scale Behavior | Main Strength | Main Weakness |
|---|---|---|---|---|---|---|---|
| `clipboardx` | Single SQLite table `clipboard_history` | Load recent `N` rows, then in-memory filter | Time index only | Eager load of recent history into memory | Degrades as `N` and text length grow | Simple, local, easy to reason about | No DB-side full-text search |
| `CopyQ` | Local tab storage files + item data storage | Load tab items into model, then filter model | In-memory text / regex matching | Model-driven, largely memory-backed filtering | Better UI behavior than naive filtering, but still not true indexed search | Mature UI handling for large lists | Still not a persistent full-text index architecture |
| `qollect` | SQLite main tables | Query through SQLite FTS5 | FTS5 + normal B-tree indexes | Search can stay DB-side | Much better fit for growing history | Practical local desktop design | More schema and index maintenance complexity |
| `Orca Note` | Repo-local SQLite block DB | Query block tables and dedicated FTS virtual tables | FTS5 + relational tables + triggers | DB-centric, incremental index sync | Scales better than memory scan for text search | Clean block model, alias search, trigger-based sync | More moving parts than a flat clipboard table |
| `SiYuan` | Multiple SQLite DBs by domain | Query different FTS domains by content type | Multiple FTS5 domains | Split by concern, not one giant table | Strong long-term maintainability | Domain-specific search and storage separation | Higher architecture complexity |
| Quicker file archive + `rg` | Filesystem, often time-partitioned | Scan matching files at query time | No persistent inverted index | No preload required | Good when time range is narrowed first | Transparent, scriptable, easy to back up | Full-history free-form search is weaker than indexed search |

## Tool Notes

## `clipboardx`

Current observed design:

- Storage is a single SQLite file with one main history table.
- The table has a time index on `copied_at_ms`.
- Search is not SQLite FTS.
- The app loads the newest `MaxItems` into memory and filters `_allItems` in process.
- Matching is `Contains(...)` plus runtime pinyin blob generation for Chinese search.

Implications:

- Good for small to moderate history sizes.
- Weak fit for very large history sizes when unrestricted text search is common.
- Search cost grows with item count and text size because the path is effectively a full scan of loaded items.

## `CopyQ`

Observed design direction:

- Items are loaded into an application model.
- Search/filter operates on the model rather than a DB-side full-text index.
- UI filtering is more mature than a simple direct scan:
  - debounce exists
  - filtering is done in batches to keep UI responsive

Implications:

- Better perceived responsiveness than a naive in-memory filter.
- Still not the same thing as a persistent full-text index.
- More suitable than `clipboardx` for larger lists from a UI engineering perspective, but not the strongest architecture for long-term full-history search.

## `qollect`

Observed local DB design:

- SQLite main table for clipboard items.
- Separate table for raw formats.
- Normal indexes for structural fields such as hash, timestamps, type, pinned state, workbench id.
- FTS5 virtual table for text search:
  - content tokens
  - OCR tokens
  - note tokens

Implications:

- This is a strong reference design for a local clipboard manager.
- It keeps SQLite as the source of truth while using FTS5 for real full-text search.
- It also distinguishes between plain content, OCR, and note-like metadata instead of forcing all search into a single generic field.

## `Orca Note`

Observed repo DB design:

- Content is block-based.
- Structure is normalized:
  - `Block`
  - `BlockAlias`
  - `BlockProperty`
  - `BlockRef`
  - `BlockRefData`
- Search is split into dedicated FTS virtual tables:
  - `BlockFTS`
  - `BlockAliasFTS`
- FTS is kept in sync with triggers on insert, delete, and update.
- Alias model includes a generated pinyin field.

Implications:

- Strong example of:
  - source tables for truth
  - dedicated FTS tables for search
  - trigger-driven incremental indexing
  - precomputed search-friendly fields
- This is highly relevant for clipboard history if features like aliases, notes, tags, or pinyin-aware search matter.

## `SiYuan`

Observed DB layout:

- Not one DB for everything.
- Separate DBs by concern:
  - `siyuan.db` for core content
  - `blocktree.db` for tree/navigation structure
  - `asset_content.db` for asset text search
  - `history.db` for history search
- Separate FTS domains for different search surfaces:
  - blocks
  - assets
  - histories
- Case-insensitive FTS variants are maintained explicitly.

Implications:

- This is a mature long-term architecture.
- It avoids mixing navigation, primary content, asset text, and history into one giant search table.
- It shows that domain-specific search indexes can be more maintainable than one monolithic index.

## Quicker File Archive + `rg` / `ugrep` / `fzf`

Observed pattern:

- Clipboard entries are archived into the filesystem, often partitioned by day.
- Search is done by scanning files on demand.
- Time can be used as a natural partition before content search.

Implications:

- Very strong for:
  - transparency
  - backup portability
  - shell integration
  - auditability
- Especially effective when queries are usually scoped by date first.
- Weaker than indexed search when the query is:
  - full-history
  - content-first
  - frequent
  - latency-sensitive

## Patterns Worth Borrowing

## Strong patterns

| Pattern | Seen In | Why It Matters |
|---|---|---|
| SQLite as local source of truth | `clipboardx`, `qollect`, `Orca`, `SiYuan` | Simple deployment and backup story |
| Separate FTS layer instead of table scan | `qollect`, `Orca`, `SiYuan` | Better search scaling |
| Multiple search domains | `qollect`, `SiYuan` | OCR, notes, history, main content do not need to share one index |
| Trigger-driven index sync | `Orca` | Keeps search index in sync automatically |
| Precomputed search fields | `Orca`, `clipboardx` conceptually | Better than rebuilding pinyin / normalized text on every query |
| Time-partitioned cold storage | Quicker-style archive | Strong for long-term retention and external tooling |

## Weak patterns for large history

| Pattern | Risk |
|---|---|
| Load all recent items and scan in memory | Memory growth and search latency growth |
| Single flat table with no text index | Poor unrestricted search scalability |
| One giant mixed search corpus with no domain split | Harder ranking, harder maintenance, noisier results |

## Practical Recommendation For `clipboardx`

Recommended target architecture:

1. Keep SQLite as the source of truth.
2. Add SQLite FTS5 for DB-side full-text search.
3. Precompute normalized text fields on write:
   - lowercase / normalized content
   - optional pinyin search field
4. Split search domains when features grow:
   - main clipboard text
   - OCR / extracted text
   - notes / tags
   - optional archived history
5. Keep a small hot in-memory cache for UI smoothness:
   - recent 500 to 2000 items
6. Optionally add a cold archive layer:
   - monthly or daily `jsonl` / `ndjson`
   - for portability and low-risk long-term retention

## Recommended shape

| Layer | Suggested Role |
|---|---|
| Hot memory cache | Fast popup rendering and recent-item operations |
| Main SQLite tables | Truth for item data, timestamps, type, source app, tags, notes, hashes |
| FTS5 virtual tables | Actual full-text retrieval |
| Optional archive files | Cold retention, external grep workflows, backup portability |

## Candidate Direction Ranking

For a local long-term clipboard manager focused on search:

1. `SQLite + FTS5 + hot cache`
2. `SQLite + FTS5 + split search domains`
3. `SQLite + FTS5 + cold archive files`
4. `Time-partitioned files + rg/fzf` as the primary system only if workflows are mostly date-scoped
5. External search engine only if the product becomes multi-device or service-oriented

## Takeaway

The most useful lesson from the compared systems is not "pick one magical database".

The strongest common direction is:

- keep local structured truth
- avoid in-memory full-history scans for search
- maintain dedicated text indexes
- split indexes by search domain when the product grows
- keep archive/export paths simple

For `clipboardx`, the most credible next step is not a jump to a complex distributed search stack. The most credible next step is moving from:

- `SQLite table + in-memory scan`

to:

- `SQLite tables + FTS5 + precomputed search fields + optional archive layer`

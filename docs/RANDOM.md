select count(id) Methods, avg(line_end - line_start) AverageLoc -- we can have it as computed column
from codememory.symbols
where kind = 'Method' -- Roslyn report kind as Method and Tree Sitter as Function
--should store language type? or define our own Kinds / abstraction instead of relying on underlying (lets stay away from it and simply define abstracted view)

select top 5 * from codememory.symbols
-- generation 1 - generation 2 - generation 3 (like that Covid spread visualization) the more depth, the more complex the repo/project is
-- number of edges will be proportional to complexity; like that cyclometic thing
-- dead symbols; those lone symbols with no edges
-- symbols per file classification / symbol relatives - edges - families clustering

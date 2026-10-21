"""Generate docs/ERD.md (Mermaid ER diagrams) from a tenant database's schema.

Usage:  python -I tools/erd/generate_erd.py [server] [database]   (defaults: localhost jaftim-local-db)

Read-only: it runs SELECTs against sys.* views only. Point it at a LOCAL database (CLAUDE.md rule 3).

The legacy schema declares few foreign keys, so relationships come from three sources, in order of precedence:
1. declared FKs (solid lines);
2. JOIN conditions mined from the procedures/views: a column equated with another table's single-column PK
   (`BPD.Base_DischargePort_Id = SSD.Stock_S_D_DischargeId`) - the most frequent partner wins;
3. the column name (FK_Base_Model_Id -> Base_Model, Stock_SP_MakeId -> Base_Make, CustomerId -> UserProfile).
2 and 3 are drawn dotted.
"""
import re
import subprocess
import sys
from collections import Counter, defaultdict
from pathlib import Path

SERVER = sys.argv[1] if len(sys.argv) > 1 else "localhost"
DB = sys.argv[2] if len(sys.argv) > 2 else "jaftim-local-db"
OUT = Path(__file__).resolve().parents[2] / "docs" / "ERD.md"

COLS_SQL = """SET NOCOUNT ON; SELECT t.name, c.name, ty.name,
 CASE WHEN EXISTS(SELECT 1 FROM sys.indexes i JOIN sys.index_columns ic ON ic.object_id=i.object_id AND ic.index_id=i.index_id
   WHERE i.is_primary_key=1 AND i.object_id=t.object_id AND ic.column_id=c.column_id) THEN 1 ELSE 0 END
 FROM sys.tables t JOIN sys.columns c ON c.object_id=t.object_id JOIN sys.types ty ON ty.user_type_id=c.user_type_id
 ORDER BY t.name, c.column_id"""
FKS_SQL = """SET NOCOUNT ON; SELECT OBJECT_NAME(fk.parent_object_id), cp.name, OBJECT_NAME(fk.referenced_object_id)
 FROM sys.foreign_keys fk JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id=fk.object_id
 JOIN sys.columns cp ON cp.object_id=fkc.parent_object_id AND cp.column_id=fkc.parent_column_id
 JOIN sys.columns cr ON cr.object_id=fkc.referenced_object_id AND cr.column_id=fkc.referenced_column_id
 WHERE cr.name <> 'CompanyId'"""
DEFS_SQL = """SET NOCOUNT ON; SELECT REPLACE(REPLACE(m.definition, CHAR(13), ' '), CHAR(10), ' ')
 FROM sys.sql_modules m JOIN sys.objects o ON o.object_id = m.object_id WHERE o.type IN ('P', 'V', 'FN', 'IF', 'TF')"""

# Dead/scratch tables (docs/DATABASE.md) and the unused ASP.NET Identity tables are left out.
EXCLUDE = re.compile(r"^(bk.*|Test.*|test_.*|Testing|Payment|RefundClaim.*|EmployeePolicy.*|old_Inquiry|"
                     r"Temp_BulkData|Debug_GetPublicMethodData|__EFMigrationsHistory|AspNet.*)$")
# Columns that hold a UserProfile.UserProfileId by convention (customers, staff and agents are all UserProfile rows).
USER_COLS = {"userprofileid", "customerid", "userid", "salespersonid", "agentid", "approvedby", "approverid",
             "assignedto", "assigneduserid", "reportstouserid", "requestedby", "actionby", "recipientuserid"}
# On nearly every table; drawing them would bury everything else.
SKIP_COLS = {"createdby", "modifiedby", "updatedby", "deletedby", "companyid"}
INT_TYPES = {"int", "bigint", "smallint", "tinyint"}

DOMAINS = [
    ("Stock: sales, reservation and money", r"^(Stock_(Reservation|Financial|Allocation|Cancellation|Refund|"
                                            r"Discount|Deallocation|CustomerOrder|Adjustment|Modification|"
                                            r"Cost_Detail)|prc_stock|CustomerFavouriteStock)"),
    ("Stock: logistics and documents", r"^(Stock_(Shipment|BL_|Port_|Courier|Inspection|Image|File|Folder|Videos|"
                                       r"InternalNote)|StockImages|StockWorkflow|StockStatusRefresh|web_stock)"),
    ("Stock: vehicle, status and lifecycle", r"^(Stock$|Stock_|StockJourney|StockGarageVisit|StockType|"
                                             r"garagevisitype|Supplier)"),
    ("Customers, wallets and finance", r"^(Customer|CompanyIncome|CurrencyExchange|BankStatement|DiscoutRequest)"),
    ("Users, roles and security", r"^(UserProfile|UserRole|Role|UserType|UserHierarchy|UserReporting|UserAuth|"
                                  r"UserCountries|UserEntities|Entity|DataAccessScope|LoginAttempts|"
                                  r"RegisteredDevices|SYS_|ActionURlTbl|AuthorizationKeys|WEB_ApiKeys|"
                                  r"NavigationItem|AuditLog)"),
    ("Inquiries, leads and notifications", r"^(Inquiry|Leads|Notification|EmailOutbox|SortOptions|SaveComparisons)"),
    ("Shipping", r"^(Shipping|ShipmentParty)"),
    ("Transport quoting", r"^Transport"),
    ("Vendors", r"^Vendor"),
    ("Reports", r"^Report"),
    ("respond.io integration", r"^tpi_"),
    ("Lookup tables", r"^(Base_|BaseBodyType|base_sync)"),
    ("Other", r""),
]


def query(sql):
    out = subprocess.run(["sqlcmd", "-S", SERVER, "-E", "-d", DB, "-W", "-s", "|", "-h", "-1", "-Q", sql],
                         capture_output=True, text=True, check=True).stdout
    return [line.split("|") for line in out.splitlines() if line.strip()]


def module_definitions():
    out = subprocess.run(["sqlcmd", "-S", SERVER, "-E", "-d", DB, "-y", "0", "-Q", DEFS_SQL],
                         capture_output=True, text=True, check=True, encoding="utf-8", errors="replace").stdout
    return [line for line in out.splitlines() if line.strip()]


def norm(s):
    return re.sub(r"[^a-z0-9]", "", s.lower())


def anchor(heading):
    return re.sub(r"[^a-z0-9 -]", "", heading.lower()).replace(" ", "-")


def mermaid_name(s):
    return re.sub(r"[^A-Za-z0-9_]", "_", s)


tables = defaultdict(list)  # table -> [(column, type, is_pk)]
for t, c, ty, pk in query(COLS_SQL):
    if not EXCLUDE.match(t):
        tables[t].append((c, ty, pk == "1"))
pks = {t: [c for c, _, pk in cols if pk] for t, cols in tables.items()}

columns = {t: {c.lower(): (c, ty, pk) for c, ty, pk in cols} for t, cols in tables.items()}
by_lower = {t.lower(): t for t in tables}
single_pk = {t: pk[0] for t, pk in pks.items() if len(pk) == 1}


def is_candidate(t, c):
    """An integer, non-audit column that is not the table's whole primary key."""
    _, ty, _ = columns[t][c.lower()]
    return ty in INT_TYPES and norm(c) not in SKIP_COLS and single_pk.get(t, "").lower() != c.lower()


# 2. JOIN mining: alias.Col = alias.Col where one side is a table's single-column PK.
TABLE_REF = re.compile(r"\b(?:FROM|JOIN|UPDATE|INTO)\s+(?:\[?dbo\]?\.)?\[?(\w+)\]?(?:\s+(?:AS\s+)?\[?(\w+)\]?)?", re.I)
EQUALITY = re.compile(r"\[?(\w+)\]?\.\[?(\w+)\]?\s*=\s*\[?(\w+)\]?\.\[?(\w+)\]?")
join_votes = defaultdict(Counter)  # (child, column) -> Counter(parent)
for definition in module_definitions():
    aliases = defaultdict(set)
    for table, alias in TABLE_REF.findall(definition):
        if table.lower() in by_lower:
            aliases[table.lower()].add(by_lower[table.lower()])
            if alias:
                aliases[alias.lower()].add(by_lower[table.lower()])

    def owners(alias, col):
        return [t for t in aliases.get(alias.lower(), ()) if col.lower() in columns[t]]

    for a1, c1, a2, c2 in EQUALITY.findall(definition):
        for (pa, pc), (ca, cc) in (((a1, c1), (a2, c2)), ((a2, c2), (a1, c1))):
            parents = [t for t in owners(pa, pc) if single_pk.get(t, "").lower() == pc.lower()]
            children = [t for t in owners(ca, cc) if is_candidate(t, cc)]
            if len(parents) == 1 and len(children) == 1 and parents[0] != children[0]:
                child_col = columns[children[0]][cc.lower()][0]
                join_votes[(children[0], child_col)][parents[0]] += 1

# 3. Names a referencing column may use: the parent's PK name, <Table>Id (for tables keyed on plain "Id"), and
# the same without the Base_ prefix of lookup tables (Stock_SP_MakeId -> MakeId -> Base_Make).
key_index = {"userprofileid": "UserProfile"}
for t, pk in sorted(single_pk.items()):
    names = {norm(t) + "id"} if pk.lower() == "id" else {norm(pk), norm(t) + "id"}
    if t.startswith("Base_"):
        names.add(norm(t[5:]) + "id")
    for n in names:
        key_index.setdefault(n, t)


def name_match(c):
    parts = c.split("_")
    for i in range(len(parts)):  # try the whole name, then drop leading prefix segments: Stock_R_D_CustomerId
        n = norm("".join(parts[i:]))
        n = n[2:] if n.startswith("fk") else n
        if n in USER_COLS:
            return "UserProfile"
        if n in key_index:
            return key_index[n]
    return None


rels = {}  # (child, column) -> (parent, source) ; source: "fk" | "join" | "name"
for child, col, parent in query(FKS_SQL):
    if child in tables and parent in tables:
        rels[(child, col)] = (parent, "fk")
for t, cols in tables.items():
    for c, _, _ in cols:
        if (t, c) in rels or not is_candidate(t, c):
            continue
        if join_votes[(t, c)]:
            rels[(t, c)] = (join_votes[(t, c)].most_common(1)[0][0], "join")
        elif (parent := name_match(c)) and parent != t:
            rels[(t, c)] = (parent, "name")

by_domain = defaultdict(list)
for t in sorted(tables, key=str.lower):
    by_domain[next(d for d, rx in DOMAINS if re.match(rx, t))].append(t)


def entity(t, full):
    rows = [f"        {ty} {mermaid_name(c)}{' ' + ','.join(k for k, on in (('PK', pk), ('FK', (t, c) in rels)) if on) if pk or (t, c) in rels else ''}"
            for c, ty, pk in tables[t] if full or pk or (t, c) in rels]
    return [f"    {mermaid_name(t)} {{"] + (rows or [f"        {tables[t][0][1]} {mermaid_name(tables[t][0][0])}"]) + ["    }"]


counts = Counter(src for _, src in rels.values())
md = [
    "# Entity-relationship diagrams (tenant database)",
    "",
    f"Generated by `tools/erd/generate_erd.py` from `{DB}`: {len(tables)} tables, {len(rels)} relationships "
    f"({counts['fk']} declared FKs, {counts['join']} mined from procedure JOINs, {counts['name']} from column names).",
    "Do not edit by hand - re-run the script after a schema change.",
    "",
    "- **Solid line** = declared foreign key. **Dotted line** = inferred, because the legacy schema declares few FKs:",
    "  either the column is equated with that table's primary key in a procedure/view JOIN",
    "  (`Base_DischargePort_Id = Stock_S_D_DischargeId`), or, failing that, its name matches (`Stock_SP_MakeId` ->",
    "  `Base_Make`, `CustomerId` -> `UserProfile`). An inferred link is not a constraint - confirm it before relying on it.",
    "- Audit columns (`CreatedBy`, `ModifiedBy`, ...) and `CompanyId` are on nearly every table and are not drawn.",
    "- Each diagram shows its domain's tables in full; tables it references from other domains appear as key-only stubs.",
    "  The lookup-table diagram shows keys only.",
    "- Dead/scratch tables (`docs/DATABASE.md`) and the unused `AspNet*` tables are omitted.",
    "",
    "## Contents",
    "",
]
md += [f"- [{d}](#{anchor(d)}) - {len(by_domain[d])} tables" for d, _ in DOMAINS if by_domain[d]]
md.append("")

for d, _ in DOMAINS:
    own = by_domain[d]
    if not own:
        continue
    links = sorted((c, col, p, src == "fk") for (c, col), (p, src) in rels.items() if c in own)
    stubs = sorted({p for _, _, p, _ in links if p not in own}, key=str.lower)
    md += [f"## {d}", "", "```mermaid", "erDiagram"]
    for t in own:
        md += entity(t, full=d != "Lookup tables")
    for t in stubs:
        md += entity(t, full=False)
    md += [f"    {mermaid_name(p)} ||{'--' if dec else '..'}o{{ {mermaid_name(c)} : \"{col}\"" for c, col, p, dec in links]
    md += ["```", ""]

conflicts = sorted((k, v) for k, v in join_votes.items() if len(v) > 1 and rels.get(k, (None, ""))[1] == "join")
md += ["## Columns joined to more than one table", "",
       "The procedures join these columns to different tables (polymorphic values or legacy inconsistency). The",
       "diagram draws the most frequent partner only; the counts are JOIN occurrences across procedures and views.", "",
       "| Column | Joined to |", "|---|---|"]
md += [f"| `{t}.{c}` | {', '.join(f'`{p}` ({n})' for p, n in v.most_common())} |" for (t, c), v in conflicts]
md.append("")

related = {c for c, _ in rels} | {p for p, _ in rels.values()}
md += ["## Tables with no detected relationship", "",
       ", ".join(f"`{t}`" for t in sorted(tables, key=str.lower) if t not in related), ""]

OUT.write_text("\n".join(md), encoding="utf-8")
print(f"{len(tables)} tables, {dict(counts)} -> {OUT}")

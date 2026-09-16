"""
Badmintonportalen.no (Cup2000) unofficial API client.
Base URL: https://badmintonportalen.no
ASMX endpoint: /SportsResults/Components/WebService1.asmx/<MethodName>
All POSTs require JSON body with callbackcontextkey (session token from page HTML).

Session setup:
  1. GET /NBF/Ranglister/ → saves ASP.NET_SessionId cookie and SR_CallbackContext token
  2. Use cookie + token for all ASMX calls

Known RankingListTable (agegroupid, name, rankinglistid, defaultRankingListId):
  SEN Herresingel:    rankinglistid=1  (agegroupid=2001)
  SEN Herredouble:    rankinglistid=2
  SEN Damesingel:     rankinglistid=3
  SEN Damedouble:     rankinglistid=4
  SEN Mixeddouble D:  rankinglistid=5
  SEN Mixeddouble H:  rankinglistid=6
  (see RankingListTable in page HTML for full list up to id=90)

Norwegian season IDs: 2002013 = 2013/2014, 2002025 = 2024/2025 (format: 200<year>)

KEY FINDING — optional int params must be "" not 0:
  ASP.NET AJAX deserializes "" as null for Nullable<int> parameters.
  Passing 0 instead of "" causes a server-side type mismatch → HTTP 500.
  GetRankingListPlayers and GetRankingListPlayersSenior require this for all
  optional filter parameters (agegroupid, clubid, regionid, pointsfrom, etc.).
"""

import re
import requests

BASE = "https://badmintonportalen.no"
ASMX = f"{BASE}/SportsResults/Components/WebService1.asmx"
RANKING_PAGE = f"{BASE}/NBF/Ranglister/"

HEADERS = {
    "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36",
    "Accept": "application/json, text/javascript, */*; q=0.01",
    "Accept-Language": "no-NO,no;q=0.9,en-US;q=0.8,en;q=0.7",
    "X-Requested-With": "XMLHttpRequest",
    "Sec-Fetch-Dest": "empty",
    "Sec-Fetch-Mode": "cors",
    "Sec-Fetch-Site": "same-origin",
}


class BadmintonportalenClient:
    def __init__(self):
        self.session = requests.Session()
        self.token = None
        self._init_session()

    def _init_session(self):
        """Fetch ranking page to get session cookie and callbackcontextkey token."""
        resp = self.session.get(
            RANKING_PAGE,
            headers={
                "User-Agent": HEADERS["User-Agent"],
                "Accept": "text/html,application/xhtml+xml,*/*",
                "Sec-Fetch-Dest": "document",
                "Sec-Fetch-Mode": "navigate",
                "Sec-Fetch-Site": "none",
            },
        )
        resp.raise_for_status()
        m = re.search(r"SR_CallbackContext = '([^']+)'", resp.text)
        if not m:
            raise RuntimeError("Could not find SR_CallbackContext token in page")
        self.token = m.group(1)

    def _post(self, method: str, params: dict) -> dict:
        payload = {"callbackcontextkey": self.token, **params}
        resp = self.session.post(
            f"{ASMX}/{method}",
            json=payload,
            headers={**HEADERS, "Referer": RANKING_PAGE},
        )
        resp.raise_for_status()
        data = resp.json()
        if "Message" in data and "ExceptionType" in data:
            raise RuntimeError(f"{method} failed: {data['Message']}")
        return data.get("d", data)

    def get_ranking_versions(
        self, rankinglistagegroupid: int, rankinglistid: int, seasonid: int
    ) -> list:
        """Returns list of {Text, Value, Selected} dicts for available ranking snapshots."""
        result = self._post(
            "GetRankingListVersions",
            {
                "rankinglistagegroupid": rankinglistagegroupid,
                "rankinglistid": rankinglistid,
                "seasonid": seasonid,
            },
        )
        return result.get("Versions", [])

    def search_player(
        self,
        name: str = "",
        clubid: str = "",
        playernumber: str = "",
        gender: str = "",
        agegroupid: str = "",
    ) -> str:
        """Returns HTML table of matching players.
        Each row's onclick contains: playerid, playernumber, playername, clubid, clubname, gender
        Example: SPSel1('36427', '109614', 'Brynjar Hus', '65', 'NTNUI', 'M')
        """
        result = self._post(
            "SearchPlayer",
            {
                "selectfunction": "SPSel1",
                "name": name,
                "clubid": clubid,
                "playernumber": playernumber,
                "gender": gender,
                "agegroupid": agegroupid,
                "searchteam": False,
                "licenseonly": False,
                "agegroupcontext": 0,
                "tournamentdate": "",
            },
        )
        return result.get("Html", "")

    def parse_search_results(self, html: str) -> list[dict]:
        """Parse player IDs from SearchPlayer HTML response."""
        pattern = r"SPSel1\('(\d+)',\s*'(\d+)',\s*'([^']+)',\s*'(\d+)',\s*'([^']+)',\s*'([MF])'\)"
        players = []
        for m in re.finditer(pattern, html):
            players.append(
                {
                    "playerid": int(m.group(1)),
                    "playernumber": m.group(2),
                    "name": m.group(3),
                    "clubid": int(m.group(4)),
                    "clubname": m.group(5),
                    "gender": m.group(6),
                }
            )
        return players

    def get_player_profile(
        self, playerid: int, seasonid: int = 2002025
    ) -> str:
        """Returns HTML with player profile including ranking positions."""
        result = self._post(
            "GetPlayerProfile",
            {
                "seasonid": seasonid,
                "playerid": playerid,
                "getplayerdata": True,
                "showUserProfile": False,
                "showheader": False,
            },
        )
        return result.get("Html", "")

    def parse_player_rankings(self, profile_html: str) -> list[dict]:
        """Parse ranking positions from GetPlayerProfile HTML."""
        rankings = []
        # onclick='return ShowRankingListPoints(seasonid, playerid, rankinglistid, rankinglistplayerid);'
        pattern = (
            r"ShowRankingListPoints\((\d+),\s*(\d+),\s*(\d+),\s*(\d+)\)[^>]*>"
            r"\s*(\d+)\s*</a>"               # points
            r".*?<td[^>]*>(\d+)</td>"        # rank (Plassering)
        )
        # Simpler: parse table rows
        row_pattern = (
            r"<a href='(/NBF/Ranglister/#\d+)'[^>]*>([^<]+)</a>"  # list name
            r".*?ShowRankingListPoints\((\d+),\s*(\d+),\s*(\d+),\s*(\d+)\)"  # season, player, list, listplayer
            r".*?'[^']*'>(\d+)</a>"  # points
            r".*?<td[^>]*>(\d+)</td>"  # rank
        )
        for m in re.finditer(row_pattern, profile_html, re.DOTALL):
            rankings.append(
                {
                    "rankinglist_url": m.group(1),
                    "rankinglist_name": m.group(2),
                    "seasonid": int(m.group(3)),
                    "playerid": int(m.group(4)),
                    "rankinglistid": int(m.group(5)),
                    "rankinglistplayerid": int(m.group(6)),
                    "points": int(m.group(7)),
                    "rank": int(m.group(8)),
                }
            )
        return rankings

    def get_player_ranking_points(
        self,
        playerid: int,
        rankinglistid: int,
        rankinglistplayerid: int = 0,
        seasonid: int = 2002025,
    ) -> str:
        """Returns HTML with per-tournament points history for a player in a ranking list."""
        result = self._post(
            "GetPlayerRankingListPoints",
            {
                "seasonid": seasonid,
                "playerid": playerid,
                "rankinglistid": rankinglistid,
                "rankinglistplayerid": rankinglistplayerid,
                "getplayerdata": True,
            },
        )
        return result.get("Html", "")

    def get_ranking_list_players(
        self,
        rankinglistid: int,
        seasonid: int = 2002025,
        rankinglistversiondate: str = "",
        pageindex: int = 0,
        getversions: bool = False,
    ) -> dict:
        """Returns {Html, Versions} for a page of ranking list players (100 per page).
        Optional numeric filters must use "" (empty string), not 0 — ASP.NET AJAX maps
        "" to null for Nullable<int> params; passing 0 causes HTTP 500.
        Use parse_ranking_list_players() to extract player dicts from Html.
        """
        result = self._post(
            "GetRankingListPlayers",
            {
                "rankinglistagegroupid": "",
                "rankinglistid": rankinglistid,
                "seasonid": seasonid,
                "rankinglistversiondate": rankinglistversiondate,
                "agegroupid": "",
                "classid": "",
                "gender": "",
                "clubid": "",
                "searchall": False,
                "regionid": "",
                "pointsfrom": "",
                "pointsto": "",
                "rankingfrom": "",
                "rankingto": "",
                "birthdatefromstring": "",
                "birthdatetostring": "",
                "agefrom": "",
                "ageto": "",
                "playerid": "",
                "param": "",
                "pageindex": pageindex,
                "sortfield": 0,
                "getversions": getversions,
                "getplayer": True,
            },
        )
        return result

    def parse_ranking_list_players(self, html: str) -> list[dict]:
        """Parse player rows from GetRankingListPlayers HTML.
        Returns list of dicts with: rank, prev_rank, playernumber, playerid,
        rankinglistid, rankinglistplayerid, seasonid, name, club, class_, points.
        Player href format: /NBF/Spiller/VisSpiller/#playerid,listid,listplayerid,seasonid
        """
        import html as html_module
        row_pattern = (
            r"<tr[^>]*>"
            r"<td class='rank'>(\d+)</td>"
            r"<td>\((\d+)\)</td>"
            r"<td class='playerid'>(\d+)</td>"
            r"<td class='name'><a href='([^']+)'[^>]*>([^<]+)</a>,\s*([^<]+)</td>"
            r"<td class='clas'>([^<]*)</td>"
            r"<td class='points[^']*'>(\d+)</td>"
            r"</tr>"
        )
        players = []
        for m in re.finditer(row_pattern, html):
            href = m.group(4)
            ids = re.search(r"#(\d+),(\d+),(\d+),(\d+)", href)
            players.append(
                {
                    "rank": int(m.group(1)),
                    "prev_rank": int(m.group(2)),
                    "playernumber": m.group(3),
                    "playerid": int(ids.group(1)) if ids else None,
                    "rankinglistid": int(ids.group(2)) if ids else None,
                    "rankinglistplayerid": int(ids.group(3)) if ids else None,
                    "seasonid": int(ids.group(4)) if ids else None,
                    "name": html_module.unescape(m.group(5).strip()),
                    "club": html_module.unescape(m.group(6).strip()),
                    "class_": m.group(7).strip(),
                    "points": int(m.group(8)),
                }
            )
        return players

    def get_all_ranking_list_players(
        self,
        rankinglistid: int,
        seasonid: int = 2002025,
        rankinglistversiondate: str = "",
    ) -> list[dict]:
        """Fetch all pages and return complete ranking list as a flat list of player dicts."""
        all_players = []
        pageindex = 0
        while True:
            result = self.get_ranking_list_players(
                rankinglistid, seasonid, rankinglistversiondate, pageindex
            )
            html = result.get("Html", "")
            players = self.parse_ranking_list_players(html)
            if not players:
                break
            all_players.extend(players)
            # Check if there's a next page link
            if f"SelectRankingListPage({pageindex + 1})" not in html:
                break
            pageindex += 1
        return all_players


# ── Demo ─────────────────────────────────────────────────────────────────────

if __name__ == "__main__":
    client = BadmintonportalenClient()
    print(f"Session token: {client.token[:20]}...")

    # 1. Get ranking versions for SEN Herresingel (agegroupid=2001, listid=1)
    print("\n--- Ranking versions (SEN, latest season) ---")
    versions = client.get_ranking_versions(2001, 1, 2002025)
    for v in versions[:5]:
        print(f"  {v['Text']:15s}  value={v['Value']!r}")
    print(f"  ... ({len(versions)} total versions)")

    # 2. Search for Brynjar Hus
    print("\n--- Search: Brynjar ---")
    html = client.search_player(name="Brynjar")
    players = client.parse_search_results(html)
    for p in players:
        print(f"  {p['playerid']:6d}  {p['name']:<30s}  {p['clubname']:<20s}  {p['gender']}")

    # 3. Get Brynjar Hus profile (playerid=36427)
    print("\n--- Brynjar Hus (36427) rankings ---")
    profile = client.get_player_profile(36427)
    rankings = client.parse_player_rankings(profile)
    for r in rankings:
        print(f"  {r['rankinglist_name']:<35s}  points={r['points']:5d}  rank={r['rank']}")

    # 4. Get points history for SEN Herresingel (rankinglistid=1, rankinglistplayerid=227328)
    print("\n--- Brynjar Hus SEN Herresingel points history ---")
    pts_html = client.get_player_ranking_points(36427, 1, 227328)
    # Extract tournament rows
    for m in re.finditer(
        r"<td[^>]*>(\d{2}\.\d{2}\.\d{4})</td><td>(.*?)</td><td[^>]*>(\d+)</td>",
        pts_html,
    ):
        print(f"  {m.group(1)}  {m.group(2)[:50]:<50s}  {m.group(3)} pts")

    # 5. Get full SEN Herresingel ranking list (all pages)
    print("\n--- SEN Herresingel full ranking (page 0 sample) ---")
    result = client.get_ranking_list_players(1, 2002025, pageindex=0)
    players_p0 = client.parse_ranking_list_players(result["Html"])
    print(f"  Players on page 0: {len(players_p0)}")
    for p in players_p0[:10]:
        print(f"  #{p['rank']:3d} ({p['prev_rank']:3d})  id={p['playerid']:6d}  {p['name']:<32s}  {p['club']:<20s}  {p['points']:4d} pts")

    print("\n--- SEN Herresingel all pages ---")
    all_players = client.get_all_ranking_list_players(1, 2002025)
    print(f"  Total ranked players: {len(all_players)}")
    print(f"  Last ranked: #{all_players[-1]['rank']}  {all_players[-1]['name']}  {all_players[-1]['points']} pts")

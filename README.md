# Drvrs — Darmowy Egzamin Teoretyczny na Prawo Jazdy (Kat. B) w .NET 11

Webowa aplikacja napisana w **ASP.NET Core (.NET 11)** umożliwiająca darmowe rozwiązywanie państwowych egzaminów teoretycznych na prawo jazdy (w szczególności **kategorii B**, a także pozostałych kategorii obecnych w oficjalnym katalogu) na podstawie oficjalnej bazy pytań i multimediów **Ministerstwa Infrastruktury (`gov.pl`)**.

---

## Najważniejsze cechy i wierność egzaminowi państwowemu (WORD)

1. **Identyczna struktura i punktacja jak na egzaminie państwowym**:
   - **32 pytania zamknięte** (zawsze dokładnie **1 prawidłowa odpowiedź**):
     - **Część podstawowa (20 pytań TAK/NIE)**: `10 × 3 pkt` + `6 × 2 pkt` + `4 × 1 pkt` = **46 pkt**
     - **Część specjalistyczna (12 pytań A/B/C)**: `6 × 3 pkt` + `4 × 2 pkt` + `2 × 1 pkt` = **28 pkt**
   - Maksymalna liczba punktów: **74 pkt**, wymagany próg zdawalności: **68 pkt**.
   - **Brak możliwości cofania się** do wcześniejszych lub pominiętych pytań.
2. **Identyczne limity czasowe i obsługa wideo**:
   - **Czas całkowity egzaminu: 25 minut** — po jego upływie egzamin zostaje natychmiast zakończony, a możliwość odpowiadania zablokowana.
   - **Pytania TAK/NIE z materiałem wideo**:
     - Film **nigdy nie uruchamia się automatycznie** — zdający ma **20 sekund** na przeczytanie pytania i kliknięcie w obraz wideo, aby rozpocząć projekcję.
     - Film odtwarzany jest w całości do końca, a **czas trwania filmu nie wlicza się do czasu na odpowiedź**.
     - Po zakończeniu filmu uruchamia się **15 sekund** na wskazanie odpowiedzi (`TAK` / `NIE`).
   - **Zwykłe pytania TAK/NIE** (ze zdjęciem lub bez załącznika): **35 sekund** (`20 s + 15 s`).
   - **Pytania specjalistyczne A/B/C**: **50 sekund** łącznie na przeczytanie i odpowiedź.
3. **Lekka baza SQLite i automatyczny import oficjalnego pliku Excel (`.xlsx`)**:
   - Aplikacja zawiera własny strumieniowy parser OpenXML (`ZipArchive` + `XmlReader`), który bez ciężkich bibliotek zewnętrznych konwertuje oficjalny plik `.xlsx` (~1.2 MB, 3 518 pytań) bezpośrednio do indeksowanej bazy **SQLite (`drvrs.db`, ~2.5 MB)** w ok. **150 ms**.
   - Obsługuje również oficjalne tłumaczenia pytań obecne w arkuszu ministerstwa (**PL / EN / DE / UA**).
4. **Automatyczna konwersja filmów `.wmv` → `.mp4 (H.264)` przez `ffmpeg`**:
   - Archiwa ministerstwa zawierają klipy w przestarzałym formacie Windows Media Video (`.wmv`), którego współczesne przeglądarki nie odtwarzają natywnie.
   - `MediaService` automatycznie transkoduje pliki `.wmv` do `MP4 (H.264)` w locie (z pamięcią podręczną na dysku oraz wstępnym przygotowaniem klipów w tle w momencie wylosowania egzaminu) oraz poprawnie dekoduje polskie znaki w nazwach plików z archiwów ZIP (`CP852` / `UTF-8`).
5. **Dodatkowy tryb nauki i lokalna historia podejść**:
   - Wyszukiwarka wszystkich pytań z bazy z filtrowaniem po kategorii, zakresie i punktacji (`1–3 pkt`) oraz lokalna historia wyników w przeglądarce bez konieczności rejestracji.

---

## Wariant 1: Proste hostowanie bezpośrednio na serwerze Linux (bez Dockera)

### 1. Wymagania na serwerze Linux (np. Ubuntu / Debian)
- Zainstalowane środowisko uruchomieniowe **.NET 11** (`aspnetcore-runtime-11.0` lub `dotnet-sdk-11.0`)
- Pakiet **`ffmpeg`** (do konwersji klipów `.wmv` na przeglądarkowe `.mp4`):
  ```bash
  sudo apt-get update && sudo apt-get install -y ffmpeg
  ```

### 2. Kompilacja aplikacji
```bash
dotnet publish src/Drvrs.Web/Drvrs.Web.csproj -c Release -o /opt/drvrs
```

### 3. Pobranie bazy pytań i multimediów z `gov.pl` (automatycznie lub ręcznie)

#### Opcja A — Automatyczne pobranie z poziomu CLI aplikacji (zalecane)
Aplikacja potrafi sama pobrać arkusz `.xlsx` oraz oba archiwa `.zip` ze strony ministerstwa, spłaszczyć podfoldery do `data/media/` i **natychmiast usunąć pobrane pliki `.zip` po rozpakowaniu**, aby zwolnić miejsce na dysku:

```bash
# Pobranie samych pytań (.xlsx -> SQLite, ~1.2 MB):
dotnet /opt/drvrs/Drvrs.Web.dll --sync-questions

# Pobranie pytań + obu archiwów multimediów (~9.5 GB, po rozpakowaniu pliki .zip są trwale usuwane):
dotnet /opt/drvrs/Drvrs.Web.dll --sync-all

# Opcjonalnie z flagą --convert-wmv (od razu zamienia wszystkie .wmv na .mp4 i usuwa .wmv, zmniejszając zajętość dysku z ~9.5 GB do ~2.5 GB):
dotnet /opt/drvrs/Drvrs.Web.dll --sync-all --convert-wmv
```
*(Możesz też uruchomić synchronizację jednym kliknięciem w interfejsie WWW w zakładce **„Baza i multimedia”**).*

#### Opcja B — Ręczne wgranie plików na serwer
Jeśli wolisz wgrać pliki samodzielnie (np. przez `scp` / `rsync`), umieść je w katalogu danych (`data/` względem katalogu roboczego lub ścieżki wskazanej w `Drvrs__DataDirectory`):
- Plik Excel (`.xlsx`): wrzuć do `data/questions/` (lub bezpośrednio do `data/`) — przy starcie aplikacja automatycznie wykryje plik `.xlsx` i zaimportuje go do bazy SQLite `data/drvrs.db`.
- Załączniki multimedialne (`.jpg`, `.jpeg`, `.wmv` lub przekonwertowane `.mp4`): rozpakuj do `data/media/` (aplikacja obsługuje zarówno płaską strukturę plików, jak i podfoldery `multimedia do pytań/` oraz `cz. 2/`).

### 4. Uruchomienie jako usługa `systemd`
Gotowy szablon usługi znajduje się w pliku `scripts/drvrs.service`:
```bash
sudo mkdir -p /var/lib/drvrs && sudo chown -R www-data:www-data /var/lib/drvrs
sudo cp scripts/drvrs.service /etc/systemd/system/drvrs.service
sudo systemctl daemon-reload
sudo systemctl enable --now drvrs
```

---

## Wariant 2: Niezależna instalacja w Dockerze (z automatycznym pobieraniem bazy)

Repozytorium zawiera wieloetapowy `Dockerfile` (`.NET 11` + `ffmpeg`) oraz plik `docker-compose.yml`.

### Uruchomienie jednym poleceniem
```bash
docker compose up -d --build
```

Co dzieje się automatycznie po uruchomieniu kontenera:
1. Aplikacja startuje na porcie **`8080`** (`http://localhost:8080`).
2. Gdy wolumen `/app/data` jest pusty, usługa w tle pobiera ze strony Ministerstwa Infrastruktury:
   - oficjalny plik Excel z pytaniami (`https://www.gov.pl/attachment/a5c6c329-28a5-4274-a1a8-e2813f0a51bd`) i importuje go do SQLite,
   - pierwsze archiwum multimediów (`multimedia_do_pytan.zip`), rozpakowuje wszystkie pliki do `/app/data/media/` i **natychmiast trwale usuwa plik `.zip`**, zanim przejdzie dalej,
   - drugie archiwum multimediów (`cz. 2`), rozpakowuje pliki do `/app/data/media/` i **natychmiast trwale usuwa plik `.zip`**.
3. Postęp pobierania i rozpakowywania możesz śledzić w logach kontenera (`docker logs -f drvrs-prawo-jazdy`) lub po zalogowaniu na `/admin` (jeśli ustawiono `Drvrs__AdminToken`).
4. Przy kolejnych restartach kontenera dane są już na trwałym wolumenie, więc aplikacja wstaje w ułamku sekundy bez ponownego pobierania.

### Najważniejsze zmienne środowiskowe (`docker-compose.yml` / `appsettings.json`)
| Zmienna | Domyślnie (Docker) | Opis |
| :--- | :--- | :--- |
| `Drvrs__DataDirectory` | `/app/data` | Katalog na bazę SQLite `drvrs.db`, arkusz `.xlsx` i folder `media/` |
| `Drvrs__AutoSyncQuestionsOnStartup` | `true` | Automatyczne pobranie pytań przy pustej bazie |
| `Drvrs__AutoSyncMediaOnStartup` | `true` | Automatyczne pobranie i rozpakowanie multimediów przy pustym katalogu `media/` |
| `Drvrs__ConvertWmvDuringSync` | `false` | Gdy `true`, od razu podczas wypakowywania konwertuje `.wmv` → `.mp4` i usuwa `.wmv` |
| `Drvrs__ReplaceWmvAfterTranscode` | `true` | Gdy `true`, po transkodowaniu klipu `.wmv` w locie usuwa źródłowy `.wmv`, oszczędzając ~70% miejsca |
| `Drvrs__AdminToken` | `""` | **Gdy puste (`""`), panel `/admin` i endpointy `/api/admin/*` są całkowicie wyłączone (`403`)**. Ustaw długie hasło, aby odblokować logowanie pod `/admin` |
| `Drvrs__MaxActiveExamSessions` | `2000` | Limit równoległych sesji egzaminacyjnych w RAM (ochrona przed wyczerpaniem pamięci) |
| `Drvrs__ExamStartsPerMinutePerIp` | `15` | Limit nowych egzaminów na minutę na pojedynczy adres IP użytkownika |

---

## Bezpieczeństwo przy wystawieniu przez Cloudflare Tunnel (`cloudflared`)

Aplikacja jest fabrycznie przygotowana do bezpiecznego działania w publicznym internecie za `cloudflared`:
- **Brak panelu admina dla zwykłych użytkowników**: w nawigacji publicznej nie ma żadnych przycisków administracyjnych, a publiczne `/api/status` nie ujawnia ścieżek dyskowych serwera.
- **Safe-by-default (`Drvrs__AdminToken=""`)**: dopóki nie ustawisz hasła w `Drvrs__AdminToken`, wszystkie endpointy `/api/admin/*` są całkowicie zablokowane (`403 Forbidden`), a bazę możesz synchronizować wyłącznie przez SSH (`--sync-all`).
- **Bezpieczne logowanie pod `/admin`**: po ustawieniu `Drvrs__AdminToken` wejście pod `https://twoja-domena.pl/admin` wymaga hasła weryfikowanego w stałym czasie (`CryptographicOperations.FixedTimeEquals`), wystawia podpisane ciasteczko `HttpOnly; SameSite=Strict` ważne 8 godzin i blokuje próby *brute-force* po **5 błędnych próbach na minutę** z uwzględnieniem nagłówka `CF-Connecting-IP`.
- **Nasłuch tylko na `127.0.0.1`**: zarówno `docker-compose.yml` (`127.0.0.1:8080:8080`), jak i `scripts/drvrs.service` (`http://127.0.0.1:5080`) wystawiają port wyłącznie lokalnie dla `cloudflared` (Docker nie ominie `ufw`/`iptables` na porcie zewnętrznym).
- **Dodatkowa warstwa Zero Trust (opcjonalnie)**: w panelu Cloudflare Zero Trust (*Access → Applications*) możesz dodatkowo objąć ścieżki `/admin*` oraz `/api/admin*` polityką logowania Email OTP / GitHub przed wpuszczeniem ruchu do tunelu.

---

## Uruchamianie testów automatycznych
```bash
dotnet test Drvrs.slnx
```

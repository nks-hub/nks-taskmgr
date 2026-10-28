# Seznam úkolů NKS Task Manageru

Vývoj nativní aplikace začal. Tento soubor drží plán, nálezy, review a stav ověření. Původní analýzy, RE podklady a webový návrh jsou mimo repozitář. Dokumentace v repozitáři je omezena na README a TODO. Sestavení a testy probíhají místně; CI se nyní nepřidává.

P0 znamená nutný základ nebo podmínku správnosti, P1 další základní funkci a P2 samostatné rozšíření. Role je budoucí odpovědnost, nikoli již přidělený člověk. Každá položka obsahuje závislost a podmínku dokončení. T01–T48 a T54 tvoří základní rozsah, T49–T53 rozšíření. Celkem je 54 implementačních úkolů.

## Požadavky a přijímací podmínky

- Windows 10/11 x64; správa procesů x64 i WOW64. Win7 ani hlavní aplikace pro x86 OS nejsou požadované.
- Vzhled vychází z Windows 11 Task Manageru s vlastními prvky. Výkon má vlevo jednotlivé prostředky s minigrafy a vpravo detail vybraného zařízení.
- V seznamu jsou vidět všechny disky, GPU a adaptéry včetně odpojených či nepodporovaných; nedostupný údaj není platná nula.
- Kandidáti UI: C++/WinUI, C#/WinUI s Native AOT a C++/Win32/DirectComposition. T06 je porovná nad stejným nativním jádrem a stejným ovládáním.
- Identita procesu obsahuje boot, PID a druh i hodnotu generace. Měnící akce znovu ověřují stejný cíl na stejném handlu.
- CPU používá skutečný monotónní interval a počet logických procesorů napříč skupinami; pracovní sada, soukromá paměť, procesové I/O, fyzický disk a síť jsou odlišené.
- Sběr běží mimo UI; výběr a scroll se při dávkované aktualizaci neztrácejí. Blokující podrobnosti patří do zrušitelného pracovního procesu.
- Běžný vzorek je po 1 s, rychlý pohled po 250 ms. Historie má rozpočet 64 MiB a ukazuje skutečně uchovaný rozsah a mezery.
- Výkonový referenční scénář je Release bez debuggeru, 500 živých procesů a odděleně 10 000 přehrávaných řádků. Hardware a OS se zaznamenávají s měřením mimo Git.
- Cíle: teplý start p95 do 400 ms, studený do 1,2 s, výběr p95 do 50 ms, hledání p95 do 100 ms, rolování p95 do 16,7 ms a stáří dat p95 do 1,25 s.
- Cíle za 60 s po zahřátí: vlastní CPU nejvýše 0,5 % celého PC při zobrazení a 0,1 % při minimalizaci; soukromá paměť do 220 MiB včetně výchozí historie a všech vlastních pracovníků.
- Přijetí vyžaduje funkční klávesnici, čtečku, kontrast, DPI 100/150/200 %, RDP, obnovu po ztrátě GPU a 24 hodin bez trvalého růstu paměti a handlů.
- Správní scénáře se ověřují na vlastních cílech ve VM. Součástí dokončení jsou oprávnění, odmítnutí UAC, export bez testovacích tajemství, podpis a návrat aktualizace.
- Podpora Windows 10, více skupin procesorů a WOW64 se považuje za ověřenou až po příslušném skutečném testu; lokální Windows 11 test ji nenahrazuje.

## Hotové podklady

- [x] R01 Uzavřít základní požadavky na správce procesů a systémových prostředků.
- [x] R02 Potvrdit cílovou platformu Windows 10/11 x64 se správou WOW64.
- [x] R03 Vybrat připnutou zdrojovou revizi a prověřit licenční varianty základu.
- [x] R04 Připravit požadavky na pohledy a jejich ovládání.
- [x] R05 Rozebrat rozhraní sběru, obnovy a správních akcí; podklady jsou archivované mimo Git.
- [x] R06 Připravit plán 54 implementačních úkolů a podmínky přijetí.
- [x] R07 Ověřit proklikávací návrh procesů, výkonu, služeb a detailu; tento návrh není hotová nativní aplikace.

## Průběžné nálezy a review

- [x] F01 Sestavení: MSBuild x64 načítal jiný kořen UCRT než SDK; místní build skript ověřuje skutečný SDK a nastavuje UCRTContentRoot jen pro podřízené procesy.
- [ ] F02 Výkon: měření nyní odděluje nativní dotaz, parser, systémové čítače a výpočty; lokální špičky převládají v systémových dotazech. Omezit jejich dopad a ověřit přijímací scénáře; burst test není důkaz klidové spotřeby ani výkonu UI.
- [x] F03 Review: systémové CPU nyní používá NtQuerySystemInformationEx pro každou skupinu; dvě skupiny a selhání jedné z nich pokrývají testy. Skutečný víceskupinový stroj zůstává v T41.
- [ ] F04 Identita: v T09 doplnit neměnnou identitu bootu a ověřit exporty přes změnu systémových hodin; nynější sběrač drží BootTime jen po dobu svého běhu.
- [x] F05 Review sběru: chyba obnovy inventáře už nemaže zařízení ani nepřiřazuje čítače k nejisté identitě; chybějící registrace PDH se opakuje nejvýše jednou za 15 s.
- [x] F06 Review UI: opravit dvojí vlastnictví seznamu obrázků, vyhodnocovat stáří i bez nových vzorků, kontrolovat vytvoření štětce a nezobrazovat neplatnou paměť jako nulu.
- [ ] F07 Izolace sběru: získání diskového čísla používá synchronní systémový dotaz v odděleném vlákně; v T12 doplnit pracovní proces pro možnost ukončit zaseknutého poskytovatele i při zavírání aplikace.
- [ ] F08 Sítě: přidat přepínač pro virtuální rozhraní; nyní jsou vidět všechny fyzické adaptéry včetně odpojených.
- [ ] F09 Výkon: revize 6c81228 za 60 s zobrazení a 60 s minimalizace naměřila 0,165 % a 0,069 % CPU celého PC, soukromá paměť nejvýše 75,7 MiB; ověřit také předepsaných 500 procesů a dlouhý běh v T44.
- [x] F10 GPU: PDH vrací hexadecimální písmena LUID jinou velikostí než DXGI; porovnání už velikost písmen ignoruje a test zahrnuje A–F. Živý test nyní načítá obě GPU.
- [x] F11 CPU: reset jednoho logického CPU nyní zneplatní také souhrn, i když ho růst ostatních čítačů skryje; regrese ověřuje i návrat k platnému měření.
- [x] F12 Historie: odstranit původní dokumenty, designové podklady a RE ze všech lokálních commitů. Odstraněno 19 cest; kontrola všech větví našla 0 zakázaných cest, aktuální strom zůstal totožný a záloha je mimo repo.
- [ ] F13 UI: uživatel odmítl dosavadní vzhled; dokončit pixelové porovnání fontů, ikon, rozměrů a mezer s HTML. Ověřený sběr a funkční grafy nejsou přijetí vzhledu.

## Průběžné ověření

- První Release a Debug sestavení prošla; 30 kontrol parseru a výpočtů a živý test vlastní CPU zátěže a alokace 32 MiB prošly.
- Benchmark má 10 zahřívacích a 200 měřených sběrů. Původní výstupy a údaje o sestavě zůstávají mimo Git; přijímací limity aplikace nejsou označené za splněné.
- Nezávislé read-only review nativního základu našlo chybějící větev pro více skupin CPU a doporučilo typový wrapper nativního dotazu. Opravy prošly opakovaným review bez dalšího konkrétního nálezu; testy skupin používají mock.
- První C++/Win32/Direct2D kandidát běží nad živými daty; má virtualizovanou tabulku, hledání, řazení, více výběrů, detail procesu a samostatný pohled Výkon se seznamem jednotlivých prostředků, minigrafy a velkým grafem.
- Výkon uchovává omezenou historii, ukazuje její skutečné trvání a přerušuje čáru při chybějících vzorcích. Sběr procesů a zařízení běží odděleně; každá předávací schránka má nejvýše jeden čekající snímek.
- Testy výpočtů prostředků a historie a dva živé testy sběru prošly. Test nativního okna ověřuje přepnutí pohledů, výběr identity přes obnovu, filtr PID a označení starých dat po přerušení obou sběrů.
- T06, T12, T14 a T16 zůstávají otevřené: chybí srovnání WinUI/AOT, ukončitelná izolace poskytovatelů, světlý vzhled, přístupnost a úplné přijímací scénáře. Dílčí UI není dokončená aplikace.
- Rozpracovaný Výkon má seznam s minigrafy, časovou osu, pravý panel sestavy a proklikávací karty procesů; vzhled ještě není přijatý. CPU nabízí celek, logické procesory a překrytí; síť oba směry, paměť samostatný graf stránkovacího souboru.
- Pixelové porovnání při 1440 × 920 a 100% DPI: osm statických oblastí má shodné hranice znaků, dvě navigační ikony se liší nejvýše o 1 px; celé UI tím není přijaté.
- Vykreslení celého DC odstranilo světlé stopy na hranách karet; Common Controls v6 a motiv posuvníku odstranily bílý pruh v seznamu prostředků. Ověřeno za běhu, pixelovým porovnáním a testem nativního okna.
- Release, čtyři testy sběru a výpočtů a test nativního okna prošly; při čtyřech velikostech od 720 × 500 do 1440 × 920 jsou karty procesů dosažitelné posunem a ovládání nepřekrývá záhlaví ani patičku. Skutečné 150/200% DPI zbývá ověřit.
- Historie má nejvýše 512 řad po 2 401 bodech včetně logických CPU; samotné bodové buffery zůstávají pod 64 MiB. Rozsah lze přepnout mezi 60 s a 10 min; nejde o záruku uchování celé délky při každém intervalu.

## Etapa 0: Základ a měřený prototyp

- [x] T01 P0 · Architekt: připnout MIT zdroj System Informeru a licence částí; závisí na R05; hotovo při doloženém původu každé přebírané části a oddělení GPL podkladů PH2.
- [x] T02 P0 · Vývoj: zprovoznit a připnout C++20 sestavení; závisí na T01; hotovo při opakovatelném x64 Release sestavení s verzemi nástrojů a samostatném x86 pomocníku, pokud ho vyžaduje ověřovaná funkce.
- [ ] T03 P0 · Architekt: upřesnit buildy Windows 10/11 x64 a schopnosti pro x64 i WOW64 procesy; závisí na T01; hotovo při ověřitelné matici oprávnění, čítačů, ovladače a referenční sestavě bez slibu Win7 či x86 OS.
- [ ] T04 P0 · UX/QA: projít viditelné PH2, Task Manager a výchozí System Informer; závisí na R04; hotovo při protokolu hledání, řazení, detailů, grafů, klávesnice a DPI s verzemi.
- [x] T05 P0 · Vývoj: oddělit použitelný sběr System Informeru od starých oken; závisí na T02; hotovo: upravená enumerace a phnt běží samostatně, zbývají jen vazby na NT API a procesovou haldu; původní UI se nelinkuje.
- [ ] T54 P0 · Vývoj: připravit společný nativní zdroj dat a C#/WinUI 3 Native AOT prototyp; závisí na T02,T04,T05; hotovo při stejných datech, vzhledu a funkcích jako C++ kandidát a ověřeném AOT sestavení.
- [ ] T06 P0 · UI/výkon: porovnat C++/WinUI 3, C#/WinUI 3 AOT a C++/Win32/DirectComposition; závisí na T02,T04,T05,T54; hotovo při měření celého scénáře a stabilní tabulce s více výběry, klávesnicí a přístupností.

## Etapa 1: Rychlý přehled a grafy

- [ ] T07 P0 · Jádro: zavést jednotky, zdroj, interval a stav vzorku; závisí na T03,T05; hotovo při odlišení prvního, starého, odmítnutého a nepodporovaného údaje od skutečné nuly.
- [ ] T08 P0 · Jádro: omezit velikost, opakování a dobu zpracování systémového výpisu; závisí na T05,T07; hotovo při odmítnutí vadných délek a offsetů bez zablokování obnovy.
- [ ] T09 P0 · Jádro: zavést identitu procesu a větve podle dostupnosti SequenceNumber; závisí na T03,T08; hotovo při správném rozlišení stejného PID s jinou generací a kontrole hranic názvů.
- [ ] T10 P0 · Sběr: počítat CPU ze skutečných intervalů a rozlišit metody; závisí na T07,T09; hotovo při testech prvního vzorku, resetu, uspání a skupin procesorů.
- [ ] T11 P0 · Sběr: oddělit pracovní sadu, soukromou paměť a systémovou paměť; závisí na T07,T09; hotovo při správně pojmenovaných hodnotách a ověřené známé alokaci.
- [ ] T12 P0 · Jádro: oddělit rytmus sběru, detailů a UI; závisí na T06,T08; hotovo při omezených frontách, viditelném stáří vzorku a pokračujícím seznamu při pomalém poskytovateli.
- [ ] T13 P0 · Jádro/UI: předávat dávky změn místo obnovy celé tabulky; závisí na T09,T12; hotovo při zachovaném výběru, scrollu a správném přidání i odebrání procesů.
- [ ] T14 P0 · UI: vytvořit hlavní okno ve stylu Task Manageru a virtualizovanou tabulku; závisí na T06,T13; hotovo při funkčním světlém/tmavém pohledu a vytváření pouze viditelných řádků.
- [ ] T15 P1 · UI: doplnit hledání, filtry, sloupce a stabilní řazení; závisí na T14; hotovo při hledání podle názvu/PID a zachování výběru během živých změn bez uskakování cíle pod kurzorem.
- [ ] T16 P0 · Grafy/jádro: zavést sdílenou časovou osu a omezenou historii; závisí na T07,T12,T14; hotovo při živých grafech, zachování špiček i mezer a viditelném skutečném rozsahu historie v rozpočtu 64 MiB.
- [ ] T17 P1 · UI: zavést skupiny aplikací, strom a detail na stejné obrazovce; závisí na T09,T15,T16; hotovo při správných časových vazbách rodičů a návratu bez ztráty filtru a scrollu.

## Etapa 2: Podrobnosti, události a historie

- [ ] T18 P1 · Sběr/UI: zobrazit služby a vazbu na hostitelské procesy; závisí na T07,T17; hotovo při filtrech stavů, hledání, účtech, způsobu spouštění, závislostech a odlišení služeb ve společném procesu.
- [ ] T19 P0 · Sběr: izolovat blokující dotazy na handly do pracovního procesu; závisí na T12,T17; hotovo při časovém limitu, obnovení vlastního pracovníka a nedotčeném základním sběru.
- [ ] T20 P1 · Sběr/UI: doplnit vlákna, moduly a podpisy pro x64 i WOW64; závisí na T09,T17; hotovo při správné bitové šířce struktur, čtení podrobností na vyžádání a chybách přístupu bez pádu.
- [ ] T21 P1 · Sběr/UI: doplnit čtení mapy paměti a hledání otevřených objektů; závisí na T19,T20; hotovo při omezeném čtení, zrušení úlohy a výsledcích navázaných na správnou generaci.
- [ ] T22 P1 · Sběr/grafy: doplnit GPU enginy a grafickou paměť; závisí na T03,T07,T16; hotovo při rozlišení adaptérů a enginů, správné agregaci a poctivém nepodporovaném stavu.
- [ ] T23 P0 · Sběr: ověřit čítače na českých i anglických Windows; závisí na T03,T22; hotovo při lokalizačně nezávislém výběru, validaci stavu PDH vzorků a ošetření zmizelé instance.
- [ ] T24 P1 · Sběr: připojit vlastní ETW relaci a omezený proud událostí; závisí na T09,T12; hotovo při hlášení ztrát, novém ověření stavu a zastavení pouze vlastní relace.
- [ ] T25 P1 · Sběr: přiřadit diskové a síťové přenosy k životnosti procesu; závisí na T07,T24; hotovo při rozlišení I/O od disku, známém přenosu a odmítnutí nejasné identity.
- [ ] T26 P1 · Sběr/UI: zobrazit krátce žijící procesy a ukončené položky; závisí na T09,T24; hotovo při řízeném krátkém procesu a označení mezer při výpadku zdroje.
- [ ] T27 P0 · Export: uložit snímek s výběrem polí a skrytím citlivých hodnot; závisí na T07,T17; hotovo při náhledu a absenci testovacího tajného řetězce ve výchozím exportu.
- [ ] T28 P1 · Jádro/UI: porovnat dva snímky; závisí na T09,T27; hotovo při rozlišení vzniku, konce a změny bez sloučení dvou generací stejného PID.
- [ ] T29 P1 · Grafy/UI: propojit výběr času, špičky a detail procesu; závisí na T16,T25,T26,T28; hotovo při společném kurzoru, časovém výběru a zákazu živých akcí nad historickým záznamem.
- [ ] T30 P0 · UI: vysvětlit chybějící práva, zdroje a schopnosti; závisí na T03,T18,T21,T22,T24; hotovo při pravdivé matici běžný uživatel/správce/ovladač a funkčním běhu bez ovladače.

## Etapa 3: Ověřené správní akce

- [ ] T31 P0 · Bezpečnost/jádro: navrhnout úzký protokol pomocníka; závisí na T09,T30; hotovo při definovaných příkazech, identitě cíle, limitech vstupu a výsledku každé položky.
- [ ] T32 P0 · Bezpečnost: ověřit místní kanál, klienta a autorizaci spuštění; závisí na T31; hotovo při odmítnutí cizího klienta, vzdáleného připojení a opakované zprávy bez úniku tajemství.
- [ ] T33 P0 · Jádro: ověřit cíl na stejném handlu před akcí; závisí na T09,T32; hotovo při odmítnutí změněné generace, skončeného procesu a nedostatečné identity.
- [ ] T34 P0 · Bezpečnost/UI: vyžadovat nejmenší práva a zvýšení až na vyžádání; závisí na T32,T33; hotovo při funkčním běžném startu i pokračování po odmítnutí UAC.
- [ ] T35 P1 · Jádro/UI: zavření, ukončení, pozastavení, obnovení, priorita a afinita; závisí na T33,T34; hotovo při skutečných výsledcích na vlastních cílech a ochraně kritických procesů.
- [ ] T36 P0 · Jádro/UI: doplnit omezené ukončení stromu s výsledkem každého cíle; závisí na T17,T35; hotovo při souběžném vzniku potomka, zániku rodiče a záměně PID bez cizího zásahu.
- [ ] T37 P1 · Jádro/UI: start, stop a restart služby se závislostmi; závisí na T18,T34; hotovo při testovací službě, odmítnutém právu a dosažení skutečného koncového stavu.
- [ ] T38 P1 · Jádro/UI: vytvořit dump x64 i 32bitového WOW64 procesu; závisí na T20,T33,T34; hotovo při čitelném dumpu v odpovídajícím debuggeru, správné architektuře a vysvětlení citlivého obsahu.
- [ ] T39 P0 · Bezpečnost: určit pravidla pluginů a jejich umístění; závisí na T01,T32; hotovo při zákazu pluginů v pomocníku a dohledatelné důvěře každého rozšíření.
- [ ] T40 P0 · QA: ověřit správní scénáře ve VM na vlastních procesech a službě; závisí na T35,T36,T37,T38,T39; hotovo při protokolu úspěchů, odmítnutí, zrušení a úklidu bez zásahu do cizích cílů.

## Etapa 4: Výkon, přístupnost a vydání

- [ ] T41 P0 · QA: ověřit Windows 10/11 x64 s cíli x64 i WOW64, práva a RDP; závisí na T03,T30,T40; hotovo při výsledcích každé slíbené varianty a pravdivém označení nedostupných funkcí.
- [ ] T42 P0 · UI/QA: ověřit klávesnici, čtečku, kontrast a DPI; závisí na T14,T17,T29,T35; hotovo při přístupných tabulkách i grafech a použitelném okně při 100/150/200% DPI.
- [ ] T43 P0 · QA/výkon: ověřit správnost CPU, RAM, GPU, disku a sítě; závisí na T10,T11,T22,T23,T25; hotovo při známých zátěžích, stanovených tolerancích a vysvětlených rozdílech referencí.
- [ ] T44 P0 · Výkon: splnit současně cíle startu, odezvy, CPU, paměti a grafů; závisí na T15,T16,T19,T29,T43; hotovo při měření referenční sestavy, přetížení a 24h běhu podle přijímacích podmínek v tomto souboru.
- [ ] T45 P0 · Bezpečnost/QA: ověřit export, logy a síťové chování; závisí na T27,T38,T39; hotovo při skrytí testovacích tajemství a absenci nevyžádaného uploadu.
- [ ] T46 P0 · Vydání: připravit podpis, přenosný balíček a licenční texty; závisí na T01,T02,T41; hotovo při ověřeném původu všech součástí a spuštění na čistém testovacím systému.
- [ ] T47 P0 · Vydání: zajistit obnovu po pádu, aktualizaci a návrat verze; závisí na T32,T46; hotovo při ověření podpisu aktualizace, odmítnutí cizího balíčku a návratu bez ztráty nastavení.
- [ ] T48 P0 · QA: provést přijetí základního rozsahu a uživatelských scénářů; závisí na T40,T41,T42,T43,T44,T45,T46,T47,T54; hotovo při důkazech pro T01–T48 a T54 bez vydávání dílčí verze za plné dokončení.

## Samostatná rozšíření

- [ ] T49 P2 · Jádro/bezpečnost: posoudit podepsaný ovladač jen pro doloženou mezeru; závisí na T30,T40,T41; hotovo při zdůvodnění, auditu vstupů, podpisu a testech Secure Boot/HVCI ve VM.
- [ ] T50 P2 · Jádro/UI: paměťový editor a zavření cizího handlu; závisí na T21,T33,T40; hotovo při zvláštním potvrzení, kontrole identity a VM testech poškození či odmítnutí akce.
- [ ] T51 P2 · Sběr/grafy: doplnit NPU podle dostupného hardwaru a rozhraní; závisí na T07,T22,T43; hotovo při testu skutečného zařízení a rozlišení nepodporovaných údajů.
- [ ] T52 P2 · Vývoj/QA: doplnit ARM64 a procesy emulované na ARM64; závisí na T02,T20,T38,T41; hotovo při skutečném běhu, podrobnostech a dumpu na ARM64 stroji.
- [ ] T53 P2 · Vývoj/QA: rozšířit podporu o další Windows 10/LTSC varianty mimo základní matici; závisí na T03,T06,T41; hotovo při ověřené kompatibilitě SDK, sběru, UI a distribuce.

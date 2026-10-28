# Seznam úkolů NKS Task Manageru

Výzkum je hotový; implementační úkoly čekají na zahájení vývoje podle [plánu](PLAN_IMPLEMENTACE.md) a [návrhu UI](NAVRH_UI.md).

P0 znamená nutný základ nebo podmínku správnosti, P1 další základní funkci a P2 samostatné rozšíření. Role je budoucí odpovědnost, nikoli již přidělený člověk. Každá položka obsahuje závislost a podmínku dokončení. T01–T48 tvoří základní rozsah, T49–T53 rozšíření.

## Hotové podklady

- [x] R01 Stáhnout oficiální PH2 2.39 a zdroje; archiv, podpisy, verze a SHA-256 jsou uložené.
- [x] R02 Prověřit místní Taskmgr.exe; kopie, verze, podpis a SHA-256 jsou uložené.
- [x] R03 Ověřit REA a aktualizaci; běží 6.8.0, stejná verze je na npm a v posledním GitHub vydání.
- [x] R04 Rozebrat oba cíle přes REA; uložené importy, ochranné deklarace, dvě těla PH2 a tři těla Task Manageru.
- [x] R05 Prozkoumat sběr PH2, služby, síť, ETW, podrobnosti, pluginy a správní akce; konkrétní zdroje jsou v evidenci.
- [x] R06 Připravit analýzu, audit, návrh UI, plán a úkoly; omezení běhové a uživatelské zkoušky jsou přiznaná.

## Etapa 0: Základ a měřený prototyp

- [ ] T01 P0 · Architekt: připnout MIT zdroj System Informeru a licence částí; závisí na R05; hotovo při doloženém původu každé přebírané části a oddělení GPL podkladů PH2.
- [ ] T02 P0 · Vývoj: zprovoznit a připnout nativní sestavení; závisí na T01; hotovo při opakovatelném x64 Release sestavení ze zdrojů s přesnými verzemi nástrojů.
- [ ] T03 P0 · Architekt: určit podporované Windows a matici oprávnění, čítačů a ovladače; závisí na T01; hotovo při ověřitelné matici a určené referenční sestavě.
- [ ] T04 P0 · UX/QA: projít viditelné PH2, Task Manager a výchozí System Informer; závisí na R04; hotovo při protokolu hledání, řazení, detailů, grafů, klávesnice a DPI s verzemi.
- [ ] T05 P0 · Vývoj: oddělit použitelný sběr System Informeru od starých oken; závisí na T02; hotovo při běžícím čtení skutečných procesů bez původního hlavního okna a seznamu zbývajících vazeb.
- [ ] T06 P0 · UI/výkon: změřit C++/WinUI 3 a Win32/DirectComposition prototyp stejné tabulky a grafu; závisí na T02,T04,T05; hotovo při volbě podložené startem, CPU, pamětí, plynulostí a přístupností.

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
- [ ] T16 P0 · Grafy/jádro: zavést sdílenou časovou osu, živé grafy a omezenou historii; závisí na T07,T12,T14; hotovo při odděleném sběru a kreslení, zachování špiček i mezer a rozpočtu historie 64 MiB.
- [ ] T17 P1 · UI: zavést skupiny aplikací, strom a detail na stejné obrazovce; závisí na T09,T15,T16; hotovo při správných časových vazbách rodičů a návratu bez ztráty filtru a scrollu.

## Etapa 2: Podrobnosti, události a historie

- [ ] T18 P1 · Sběr/UI: zobrazit služby a vazbu na hostitelské procesy; závisí na T07,T17; hotovo při správném životním cyklu služby a odlišení více služeb ve společném procesu.
- [ ] T19 P0 · Sběr: izolovat blokující dotazy na handly do pracovního procesu; závisí na T12,T17; hotovo při časovém limitu, obnovení vlastního pracovníka a nedotčeném základním sběru.
- [ ] T20 P1 · Sběr/UI: doplnit vlákna, moduly a stav ověření podpisu; závisí na T09,T17; hotovo při načítání na vyžádání, práci s ukončeným procesem a chybách přístupu bez pádu.
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
- [ ] T38 P1 · Jádro/UI: vytvořit dump vybraného procesu; závisí na T20,T33,T34; hotovo při čitelném dumpu, vysvětlení citlivého obsahu a podpoře zvolené bitové šířky.
- [ ] T39 P0 · Bezpečnost: určit pravidla pluginů a jejich umístění; závisí na T01,T32; hotovo při zákazu pluginů v pomocníku a dohledatelné důvěře každého rozšíření.
- [ ] T40 P0 · QA: ověřit správní scénáře ve VM na vlastních procesech a službě; závisí na T35,T36,T37,T38,T39; hotovo při protokolu úspěchů, odmítnutí, zrušení a úklidu bez zásahu do cizích cílů.

## Etapa 4: Výkon, přístupnost a vydání

- [ ] T41 P0 · QA: ověřit deklarované Windows, různá práva, RDP a ochrany systému; závisí na T03,T30,T40; hotovo při výsledcích pro každou slíbenou variantu a poctivém označení nepodporovaných funkcí.
- [ ] T42 P0 · UI/QA: ověřit klávesnici, čtečku, kontrast a DPI; závisí na T14,T17,T29,T35; hotovo při přístupných tabulkách i grafech a použitelném okně při 100/150/200% DPI.
- [ ] T43 P0 · QA/výkon: ověřit správnost CPU, RAM, GPU, disku a sítě; závisí na T10,T11,T22,T23,T25; hotovo při známých zátěžích, stanovených tolerancích a vysvětlených rozdílech referencí.
- [ ] T44 P0 · Výkon: splnit současně cíle startu, odezvy, CPU, paměti a grafů; závisí na T15,T16,T19,T29,T43; hotovo při měření referenční sestavy, přetížení a 24h běhu podle NAVRH_UI.md.
- [ ] T45 P0 · Bezpečnost/QA: ověřit export, logy a síťové chování; závisí na T27,T38,T39; hotovo při skrytí testovacích tajemství a absenci nevyžádaného uploadu.
- [ ] T46 P0 · Vydání: připravit podpis, přenosný balíček a licenční texty; závisí na T01,T02,T41; hotovo při ověřeném původu všech součástí a spuštění na čistém testovacím systému.
- [ ] T47 P0 · Vydání: zajistit obnovu po pádu, aktualizaci a návrat verze; závisí na T32,T46; hotovo při ověření podpisu aktualizace, odmítnutí cizího balíčku a návratu bez ztráty nastavení.
- [ ] T48 P0 · QA: provést přijetí základního rozsahu a uživatelských scénářů; závisí na T40,T41,T42,T43,T44,T45,T46,T47; hotovo při důkazech pro T01–T48 a bez vydávání dílčí verze za plné dokončení.

## Samostatná rozšíření

- [ ] T49 P2 · Jádro/bezpečnost: posoudit podepsaný ovladač jen pro doloženou mezeru; závisí na T30,T40,T41; hotovo při zdůvodnění, auditu vstupů, podpisu a testech Secure Boot/HVCI ve VM.
- [ ] T50 P2 · Jádro/UI: paměťový editor a zavření cizího handlu; závisí na T21,T33,T40; hotovo při zvláštním potvrzení, kontrole identity a VM testech poškození či odmítnutí akce.
- [ ] T51 P2 · Sběr/grafy: doplnit NPU podle dostupného hardwaru a rozhraní; závisí na T07,T22,T43; hotovo při testu skutečného zařízení a rozlišení nepodporovaných údajů.
- [ ] T52 P2 · Vývoj/QA: doplnit ARM64 a smíšené bitové šířky; závisí na T02,T20,T38,T41; hotovo při skutečném běhu, podrobnostech a dumpu na ARM64 stroji.
- [ ] T53 P2 · Vývoj/QA: rozšířit podporu o další Windows 10/LTSC varianty mimo základní matici; závisí na T03,T06,T41; hotovo při ověřené kompatibilitě SDK, sběru, UI a distribuce.

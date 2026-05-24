# Komentáře k řešení

## Úvod

Dokument pro popis klíčových rozhodnutí. Popisuje, jaké alternativy jsem zvažova, proč jsem se rozhodl, jak jsem se rozhodl a kde jsou kompromisy. Ještě dodám, že samozřejmě v reálu by záleželo na užití aplikace a mnoho rozhodnutí by v závislosti na něm mohlo vypadat zcela jinak. Například pokud by se appka měla dále zásadně rozrůstat, volil bych hned od začátku MVC API + React (případně jiný FE framework)

## Architektonická rozhodnutí

**Framework: Blazor Server** - hlavní alternativou bylo MVC s Razor Views nebo MVC API + React. Blazor Server byl zvolen proto, že umožňuje C# end-to-end bez samostatné API vrstvy a bez build pipeline pro frontend. React by byl neúměrně těžký pro jednostránkové UI s jedním tlačítkem a přinesl by závislost na npm. MVC + Razor Views by fungovalo, ale přidává zbytečnou vrstvu (controller, action, view) tam, kde Blazor komponenta zvládne vše přímo. Nevýhody Blazor Serveru - stavové SignalR spojení, server-side stav komponent - jsou pro localhost single-user aplikaci irelevantní.


**Detekce změn: SHA-256** - .NET má podporu hashů MD5 a SHA-256. Bajtové porovnání nedává smysl. MD5 je o malinko rychlejší, ale má potenciální kolizi - sice spíš teoretický problém, ale SHA-256 vnímám pro takovéto použití jako "good practice".

**Persistence: JSON soubory, jeden per sledovaná cesta** - zde bylo potřeba zvážit, zda mít jeden soubor pro každou cestu nebo všechno v jednom a JSON vs. XML. Jeden kombinovaný soubor je jednodušší na načítání, ale pád uprostřed zápisu by ohrozil stav všech sledovaných cest najednou. Proto jsem zvolil více souborů. XML oproti JSON nenabízí pro toto řešení žádnou výhodu. 

**Git** - celé řešení je dokumetnováno v gitu, vše ve větvi main. To by samozřejmě v reálném vývoji neobstálo, ale pro testovací appku s jedním vývojářem by jiný přístup nebyl ani možný (jiné větve, schvalování merge requestů atp)

## Rozhodnutí a kompromisy

### Nečitelné soubory během analýzy

Pokud soubor nelze přečíst (například ho zamkl jiný proces nebo chybí oprávnění), je tiše přeskočen a jeho relativní cesta je zaznamenána. `AnalysisResult` vystavuje `IReadOnlyList<string> UnreadableFiles`. UI tuto situaci zobrazí jako varování vedle normálního výsledku, například "3 soubory nešlo přečíst: foo.txt, bar.txt, baz.txt". Analýza je považována za úspěšnou (`IsSuccess = true`), dokud se podaří alespoň načíst výpis adresáře - nečitelné soubory operaci nepřeruší.

### Umístění persistence - konfigurovatelné, výchozí `data/` pod content root

Soubory stavu jsou uloženy v podsložce `data/` pod `IWebHostEnvironment.ContentRootPath` (root projektu při vývoji, publish adresář v produkci). Cesta je konfigurovatelná přes `appsettings.json`.

### Příliš velké soubory jsou považovány za nečitelné

V zadání je určeno, že se v adresáři nebudou nacházet soubory přesahující 50 MB. Pokud by se takový soubor přesto vyskytl, byl by přidán do `UnreadableFiles` společně s nedostupnými soubory. Rozlišení (příliš velký vs. zamčený/bez oprávnění) se v UI nezobrazuje - obojí se zobrazí jako "nešlo přečíst". V praxi by to samozřejmě nebylo ideální, ale pokud vycházím z textu zadání, je to spíše kontrola navíc. 

### Nečitelné soubory jsou přenášeny do dalšího snapshotu

Pokud se soubor, který byl dříve úspěšně načten, v aktuální analýze stane nečitelným (například ho zamkl jiný proces), je vyloučen ze seznamů "Odstraněno" i "Změněno" a zobrazí se pouze v upozornění `UnreadableFiles`. Předchozí `FileEntry` (s jeho hashem a verzí) je přenesen beze změny do nově uloženého snapshotu.

Kompromis: pokud se obsah souboru během nečitelnosti skutečně změnil, tato změna bude detekována až při dalším běhu, kdy bude soubor opět čitelný - a správně přiřazena jako změna oproti poslednímu známému stavu. Alternativně bych mohl považovat nečitelný soubor za smazaný, ale to ničí kontinuitu verzí u takového souboru.

### Poškozený JSON snapshotu - automatické smazání a reset

Pokud soubor snapshotu na disku obsahuje neplatný JSON (chyba, fail při zápisu, ručně nesprávně editovaný), `LoadAsync` zachytí `JsonException`, pokusí se poškozený soubor smazat (best-effort) a vrátí `(null, WasReset: true)`. Služba cestu považuje za nový první běh a vygeneruje obrazovku, jako pro dříve neznámou složku. UI zobrazí varování "snapshot byl poškozený a byl odstraněn" vedle zobrazení počátečního stavu.

Kompromis: předchozí historie sledování pro danou cestu je ztracena. 
Alternativa 1 - propagovat výjimku a blokovat všechny další analýzy
Alternativa 2 - Přejmenování rozbitého souboru na `.corrupt` pro budoucí analýzu a zároveň vygenerování nového. 

### Relativní cesty jsou zamítnuty při vstupu

Cesty, které nejsou plně kvalifikované (absolutní), jsou zamítnuty ještě před jakýmkoliv přístupem k souborovému systému. 

I přesto, že beru aplikaci jako lokální "hračku", pokud by se někdy někam nasadila, nechtěl bych uživateli dovolit vypisovat a přistupovat k adresářům relativně k produkčnímu adresáři. Zároveň není důvod pro relativní cesty povolovat - při přístupu přes web uživatel většinou lokálně ani nemá přesný přehled, kde se aplikace na disku nachází a mohlo by ho to zbytečně mást. 

### Kontrola existence adresáře je prováděna uvnitř zámku semaforu

Kontrola, zda sledovaná cesta stále existuje (`Directory.Exists`), je prováděna uvnitř zámku semaforu, po jeho získání. Tím je zajištěno, že volání `DeleteAsync` (úklid zastaralého snapshotu) a případný error throw jsou atomické vůči souběžným analýzám stejné cesty.

Jedna kontrola zůstává před získáním zámku: zda je cesta soubor (`File.Exists`). Jde o rychlou kontrolu pro jednoznačně neplatný vstup - pro cestu, která nebyla nikdy sledována jako adresář, není potřeba čistit snapshot, a držení zámku pro tuto kontrolu by přidávalo overhead pro případ, který může uživatel okamžitě opravit.

### Selhání SaveAsync vrátí výsledek analýzy s příznakem varování

Pokud uložení snapshotu selže (plný disk, oprávnění), služba zachytí IO výjimku a nastaví `SnapshotSaveFailed = true` na výsledku - výstup skenu (Added, Changed, Removed, nebo počáteční výpis) je i přesto vrácen do UI. UI zobrazí varování vysvětlující, že baseline nebyla aktualizována a příští analýza bude porovnávat oproti předchozímu snapshotu.

Kompromis: uživatel vidí výsledky aktuálního skenu, příští běh bude porovnávat oproti předcházejícímu stavu a může znovu nahlásit stejné změny. Uživatel je ale varován předem.

Dočasný `.tmp` soubor zapsaný během `SaveAsync` je při selhání odstraněn.

### DeleteAsync je synchronní metoda za asynchronním rozhraním

`DeleteAsync` používá `File.Delete` synchronně a vrací `Task.CompletedTask`. V .NET neexistuje asynchronní API pro mazání souborů - smazání je vždy synchronní. Signatura vracející `Task` je zachována kvůli konzistenci rozhraní (aby mock implementace v testech mohly být async).

Pokud smazání selže, zastaralý `.json` soubor zůstane v datovém adresáři. Toto není ideální, ale v reálu by to samozřejmě chtělo nějak ošetřit, alespoň nahlásit uživateli, že ke smazání nedošlo. V tuto chvíli je to alespoň zalogováno. 

### Trvale nečitelné soubory jsou přenášeny donekonečna

Soubor, který je v adresáři, ale trvale nečitelný, je přenášen v každém snapshotu a nikdy se neobjeví v seznamu "Odstraněno". Soubor existuje na disku, jen ho nelze přečíst. 
Alternativa - považovat "nečitelný po N po sobě jdoucích bězích" za odstraněný - by byla zbytečně složitá a je "out of scope" zadání

### Limit MaxFiles se vztahuje na cenu skenu, ne na velikost snapshotu

Limit 100 souborů (`MaxFiles`) je vynucován vůči souborům, které by byly aktivně skenovány a hashované v aktuálním běhu. Přenášené záznamy pro nečitelné soubory jsou k snapshotu přidány po této kontrole a do limitu se nezapočítávají. Toto je záměrné: kontrola existuje kvůli nákladům na hashování a přenášené záznamy nevyžadují žádné I/O - jsou kopírovány z paměti. Soubory snapshotu proto mohou obsahovat mírně více než 100 záznamů, pokud se nečitelné soubory hromadí přes více běhů. Opět limit 100 souborů vnímám spíše jako constrain vstupních dat, takže celková kontrola na 100 souborů je jenom drobný check navíc a nevadí, že v reálu nebude přesná. 

### Rozsah zachytávání výjimek v SaveAsync

`AnalyzeLockedAsync` zachytává pouze `IOException` a `UnauthorizedAccessException` z `SaveAsync`. `SaveAsync` interně používá holý `catch`/rethrow, což znamená, že jakákoliv non-IO výjimka obejde filtr a zobrazí se jako "Nastala neočekávaná chyba" v UI. 

### _locks se smažou až při vypnutí aplikace

Každý analyzovaný soubor přidá trvalý záznam do _locks. Tento slovník je vymazán až při ukončení aplikace. Pro lokální aplikaci bych řekl, že to nevadí - očekával bych, že uživatel aplikací spustí pro každé použití nebo alespoň občas vypne/restartuje počítač. Pokud by toto mělo být někde nasazené, bylo by samozřejmě třeba toto nějak řešit. 

### Délka FolderAnalysisService 

Třída FolderAnalysisService je poměrně velká. V současné podobě aplikace to snad nevadí, ale pokud by se měla ještě rozrůst, určitě bych již navrhoval ji rozdělit na více tříd. 
# Windows program pro opravu nastavení klávesnice

Operační systém Windows dlouhodobě obsahuje extrémně otravnou chybu - automaticky přidává rozložení klávesnice pro aktuální session 
bez toho, aniž by toto nastavení bylo povoleno v nastavení klávesnice.

Např. Mám v systému nastavenu ČEŠTINU a v rámci ní má dvě rozložení klávesnice - české (QUERTY) a anglické.

Když se k počítači připojím přes vzdálenou plochu, tak se do rozložení klávesnice přidá např. české (QUERTZ) nebo USA a anglické rozložení.

Prostě se obecně přidá layout - v rámci aktuální session - v nastavení je stále to samé správné.

Jsou dvě možnosti jako toto vyřešit:

* Jít do nastavení klásnice, dané nastavení ručně přidat a zase odebrat - tím zmizí.
* Restartovat počítač - tím se opět použije (zobrazí) pouze to, co je nastavené v počítači.

Na webu je 100+1 tipů jak se toho zbavit - editace registrů, různá nastavení - nic nefunguje pořádně.

Cílem je vytvořit aplikaci - utility prográmek - který při spuštění nastaví systém tak, že bude obsahovat pouze ta rozložení klávesnice, která
jsou nastavená v systému.

V další verzi pak můžeme rozšířit tento prográmek tak, že bude spuštěn rezidentně a bude sledovat nastavení klávesnice a pokud se
změní tj. Windows zase přidá nový layout v rámci session, tak se spustí a vrátí zpět nastavení takové, jaké má být. Toto
ale bude až případný krok 2.

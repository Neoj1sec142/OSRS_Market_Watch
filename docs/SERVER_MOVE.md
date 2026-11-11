

# Moving the MacBook Server
**Nothing is needed for a small few second unplug move**
--- 


## After Moving Run From Windows:

```sh
ssh neo@marks-macbook-pro.local "pmset -g batt; launchctl print system/com.nuggetcapital.marketapi | grep -E 'state =|pid =|last exit code'"
```

- We are looking for: 
```text
Now drawing from 'AC Power'
state = running
pid = <process ID>
last exit code = (never exited)
```

#### Then Wait 5min and check if a new batch came through
```sh
ssh neo@marks-macbook-pro.local "sqlite3 /Users/neo/nugget-capital/data/market.db 'SELECT Id, CompletedAtUtc, Status, ItemsCollected FROM MarketCollectionBatches ORDER BY Id DESC LIMIT 3;'"
```
# Event sweeps

On 2026-10-30 the local client failed a reported 97-run event sweep with
`ContentSweep_Request` error 500. The server log at 20:15:32 identifies
`CampaignStageExcelExt.GetCampaignStageId` called from
`ContentSweepHandler.SweepCampaign`: event content fell through the handler's
default campaign branch, which searched the wrong stage table.

The lookup occurred before the AP consume call, so this recorded failure did
not debit AP or grant sweep rewards. The request body was not retained; the
latest cleared quest before the failure was event stage `108462304`. That
downloaded stage is used as a regression fixture, rather than claiming the
log independently proves the submitted stage ID.

Event main, sub, ground, story and generic event content now route to a
dedicated event sweep path. It resolves `EventContentStageExcel`, checks the
event ID, a positive bounded count, an existing three-star clear and entrance
currency before spending. It reuses the event clear's repeatable drop rules
(`Default`, `Rare`, `Event`) and expands gacha groups separately per run.
First-clear, permanent and three-star rewards are excluded. Account exp and
history follow the existing event clear conventions.

An outer SQLite transaction covers the AP debit, reward grant, account exp
and history. The existing parcel handler joins it instead of independently
committing its cost step. Any reward failure rolls the database changes back.

`EventSweepTests` verifies 97 runs on a private in-memory SQLite database:
979 starting AP, 970 spent, 9 remaining; 97 reward entries; item totals match
the returned rewards; history increases from 1 to 98 without changing the
one-time claim stamps. Further cases check insufficient AP, an incomplete
three-star clear, invalid count/stage/event inputs, and a SQLite trigger that
forces a reward insertion failure after the AP debit. The failure case
preserves all 979 AP and the original history.

Investigation and validation reports are under the ignored
`reports/event-sweep-2026-10-30/` directory. This fix reuses existing event
drop behavior; it does not implement specialized-student bonus calculations
or otherwise complete the event reward/mission system. Handler and database
tests are separate from user verification of the in-game sweep result UI.

# Fixtures

Synthetic Firefly III API responses, in the form firefly-iii-mcp puts them in a tool result:
the API response as JSON, indented by two spaces.

They are modelled on live results measured on 2026-11-03, but no value comes from a real
administration. Only aggregate shape was taken over: field names and order, how often a field is
null, the mean length of text fields, list lengths, and generic Firefly enumerations such as
`withdrawal` or `defaultAsset`. Every name, description, note, amount, IBAN, id, date and URL
was generated.

| File | Tool | Items |
|---|---|---:|
| `search_transactions.json` | `search_transactions` | 25 |
| `list_transaction_by_account.json` | `list_transaction_by_account` | 20 |
| `list_account.json` | `list_account` | 10 |
| `get_account.json` | `get_account` | 1 |
| `list_bill.json` | `list_bill` | 23 |
| `list_budget.json` | `list_budget` | 14 |
| `list_budget_limit_by_budget.json` | `list_budget_limit_by_budget` | 4 |

Under the same rules the fixtures and the live results slim by about the same amount
(transaction lists 81–82%, the others 54–80%), which is what makes them a fair test of the
size criteria.

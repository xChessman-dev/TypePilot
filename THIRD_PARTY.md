# Сторонние компоненты

Исходники TypePilot не содержат весов нейросети или стороннего нативного движка.

| Компонент | Источник / лицензия | Использование |
|---|---|---|
| .NET / WPF / Windows Forms | [dotnet](https://github.com/dotnet/runtime/blob/main/LICENSE.TXT), MIT; [WPF](https://github.com/dotnet/wpf/blob/main/LICENSE.TXT), MIT | UI и базовые API |
| Windows Spell Checking API / UI Automation | API установленной Windows | Системные словари и чтение явного выделения |
| llama.cpp b11429 | [официальный проект](https://github.com/ggml-org/llama.cpp/tree/b11429), [MIT](https://github.com/ggml-org/llama.cpp/blob/b11429/LICENSE) | Опциональный отдельный локальный процесс |
| Qwen3-4B-GGUF / Q4_K_M | [официальная модель](https://huggingface.co/Qwen/Qwen3-4B-GGUF), [Apache-2.0](https://huggingface.co/Qwen/Qwen3-4B-GGUF/blob/bc640142c66e1fdd12af0bd68f40445458f3869b/LICENSE) | Опциональная локальная переформулировка |

Скрипт установки использует закреплённые версии и SHA-256. Нативный архив содержит свои дополнительные файлы лицензий (например LLVM OpenMP). При распространении движка или весов отдельно сохраняйте соответствующие лицензии и уведомления. Список слов и таблица распространённых опечаток TypePilot — небольшие встроенные исходные списки, не экспорт системного словаря Windows.

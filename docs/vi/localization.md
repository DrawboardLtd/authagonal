---
layout: default
title: Ban dia hoa
locale: vi
---

# Ban dia hoa

Authagonal ho tro sau ngon ngu ngay tu dau: tieng Anh, tieng Trung gian the (`zh-Hans`), tieng Duc (`de`), tieng Phap (`fr`), tieng Tay Ban Nha (`es`) va tieng Viet (`vi`). Ban dia hoa bao gom cac phan hoi API cua may chu, giao dien dang nhap va trang tai lieu nay.

## Cac ngon ngu duoc ho tro

| Ma | Ngon ngu |
|---|---|
| `en` | Tieng Anh (mac dinh) |
| `zh-Hans` | Tieng Trung gian the |
| `de` | Tieng Duc |
| `fr` | Tieng Phap |
| `es` | Tieng Tay Ban Nha |
| `vi` | Tieng Viet |

## May chu (phan hoi API)

May chu su dung tinh nang ban dia hoa tich hop cua ASP.NET Core voi `IStringLocalizer<T>` va cac tep tai nguyen `.resx`. Ngon ngu duoc chon tu tieu de HTTP `Accept-Language`.

### Nhung gi duoc ban dia hoa

- Thong bao loi xac thuc mat khau
- Nhan chinh sach mat khau (`GET /api/auth/password-policy`)
- Thong bao quy trinh dat lai mat khau (loi token, het han, thanh cong)
- Mo ta loi chung tu middleware xu ly ngoai le
- Thong bao quan ly nguoi dung quan tri (xac nhan email, xac minh, v.v.)
- Thong bao xac nhan ket thuc phien

### Nhung gi KHONG duoc ban dia hoa

- Ma `error` co the doc bang may (`"email_required"`, `"invalid_credentials"`, v.v.) — day la cac hop dong API va khong thay doi
- Ma loi OAuth/OIDC va mo ta loi danh cho nha phat trien tren cac diem cuoi token, uy quyen va thu hoi
- Thong bao nhat ky noi bo va thong bao ngoai le

### Kiem tra ban dia hoa may chu

Gui tieu de `Accept-Language` den bat ky diem cuoi nao da duoc ban dia hoa:

```bash
# English (default)
curl https://auth.example.com/api/auth/password-policy

# Simplified Chinese
curl -H "Accept-Language: zh-Hans" https://auth.example.com/api/auth/password-policy

# German
curl -H "Accept-Language: de" https://auth.example.com/api/auth/password-policy
```

### Tep tai nguyen

Tat ca cac chuoi dich cua may chu nam trong cac tep `.resx` tai `src/Authagonal.Server/Resources/`:

```
Resources/
  SharedMessages.cs          # Marker class
  SharedMessages.resx        # English (default)
  SharedMessages.zh-Hans.resx
  SharedMessages.de.resx
  SharedMessages.fr.resx
  SharedMessages.es.resx
```

## Giao dien dang nhap

Ung dung SPA dang nhap su dung [react-i18next](https://react.i18next.com/) de ban dia hoa phia may khach. Ngon ngu duoc tu dong phat hien tu cai dat `navigator.language` cua trinh duyet.

### Phat hien ngon ngu

Thu tu phat hien la:

1. **Tham so truy van** — `?lng=de` ghi de tat ca
2. **Ngon ngu trinh duyet** — `navigator.language` (tu dong)
3. **Du phong** — Tieng Anh (`en`)

### Tep dich

Cac tep JSON dich duoc dong goi cung ung dung tai `login-app/src/i18n/`:

```
i18n/
  index.ts        # i18n initialization
  en.json         # English
  zh-Hans.json    # Simplified Chinese
  de.json         # German
  fr.json         # French
  es.json         # Spanish
```

### Nhan chinh sach mat khau

Giao dien dang nhap dich cac nhan yeu cau mat khau phia may khach dua tren khoa `rule` duoc tra ve boi `GET /api/auth/password-policy`, thay vi su dung truong `label` do may chu cung cap. Dieu nay dam bao cac yeu cau mat khau luon duoc hien thi bang ngon ngu trinh duyet cua nguoi dung, ngay ca khi tieu de `Accept-Language` cua may chu khac.

### Nguoi dung goi npm

Neu ban su dung ung dung dang nhap thong qua `@drawboard/authagonal-login`, phien ban i18n duoc xuat:

```typescript
import { i18n } from '@drawboard/authagonal-login';

// Change language programmatically
i18n.changeLanguage('de');
```

## Tai lieu

Trang tai lieu su dung cach tiep can dua tren thu muc. Cac trang tieng Anh nam o thu muc goc va cac ban dich nam trong cac thu muc con theo ngon ngu (`/zh-Hans/`, `/de/`, `/fr/`, `/es/`). Mot menu tha xuong chuyen doi ngon ngu trong thanh ben cho phep chuyen doi giua cac ngon ngu.

## Them ngon ngu moi

De them ho tro cho ngon ngu moi (vi du: tieng Nhat `ja`):

### 1. May chu

Tao tep `.resx` moi bang cach sao chep tep tieng Anh va dich cac gia tri:

```
src/Authagonal.Server/Resources/SharedMessages.ja.resx
```

Them `"ja"` vao mang cac culture duoc ho tro trong `AuthagonalExtensions.cs`:

```csharp
var supportedCultures = new[] { "en", "zh-Hans", "de", "fr", "es", "ja" };
```

### 2. Giao dien dang nhap

Tao tep JSON dich moi bang cach sao chep `en.json` va dich cac gia tri:

```
login-app/src/i18n/ja.json
```

Dang ky trong `login-app/src/i18n/index.ts`:

```typescript
import ja from './ja.json';

// In the resources object:
ja: { translation: ja },
```

### 3. Tai lieu

Tao thu muc moi voi cac tep markdown da dich:

```
docs/ja/
  index.md
  installation.md
  quickstart.md
  ...
```

Them gia tri mac dinh ngon ngu trong `docs/_config.yml`:

```yaml
defaults:
  - scope:
      path: "ja"
    values:
      locale: "ja"
```

Them tuy chon ngon ngu vao bo chuyen doi trong `docs/_layouts/default.html`.

## Them chuoi moi

### May chu

1. Them khoa va gia tri tieng Anh vao `SharedMessages.resx`
2. Them cac gia tri da dich vao tep `.resx` cua tung ngon ngu
3. Su dung `IStringLocalizer<SharedMessages>` de truy cap chuoi:

```csharp
// Inject via parameter
IStringLocalizer<SharedMessages> localizer

// Use with key
localizer["MyNewKey"].Value

// With format parameters
string.Format(localizer["MyNewKey"].Value, param1)
```

### Giao dien dang nhap

1. Them khoa va gia tri tieng Anh vao `en.json`
2. Them cac gia tri da dich vao tep JSON cua tung ngon ngu
3. Su dung ham `t()` trong cac component:

```tsx
const { t } = useTranslation();

// Simple string
<p>{t('myNewKey')}</p>

// With interpolation
<p>{t('myNewKey', { name: 'value' })}</p>
```

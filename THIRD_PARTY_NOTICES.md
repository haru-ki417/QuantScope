# 使用しているソフトウェア

| ソフトウェア | 用途 | ライセンス |
| --- | --- | --- |
| [.NET](https://github.com/dotnet/runtime) / WPF | 実行環境・画面 | MIT |
| [fo-dicom](https://github.com/fo-dicom/fo-dicom) | DICOM の読み込み | Microsoft Public License (MS-PL) |
| [fo-dicom.Codecs](https://github.com/Efferent-Health/fo-dicom.Codecs) | 圧縮 DICOM の展開 | Microsoft Public License (MS-PL)（同梱のネイティブライブラリは各ライセンスに従う） |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | 画面と処理の分離 | MIT |
| [xUnit.net](https://github.com/xunit/xunit) | テスト | Apache-2.0 |

画像処理（フィルター・しきい値・距離変換・ウォーターシェッド・ラベリング・計測・k-means・CLAHE など）は、外部のライブラリを使わずに実装しています。
参考にした方法: 大津の判別分析（Otsu, 1979）、三角法（Zack ほか, 1977）、反復法（Ridler と Calvard, 1978）、
距離変換（Felzenszwalb と Huttenlocher, 2012）、周囲長の推定（Vossepoel と Smeulders, 1982）、CLAHE（Zuiderveld, 1994）、
k-means++（Arthur と Vassilvitskii, 2007）、箱型ぼかしによるガウスの近似（Kovesi, 2010）。
カラーマップ「Viridis」は matplotlib の配色（CC0）にもとづく近似です。
